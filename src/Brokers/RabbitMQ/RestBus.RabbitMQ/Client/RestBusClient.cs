using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using RabbitMQ.Client.Framing.v0_9_1;
using RestBus.Common;
using RestBus.Common.Amqp;
using RestBus.RabbitMQ;
using RestBus.RabbitMQ.ChannelPooling;
using System;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;

namespace RestBus.RabbitMQ.Client
{

    public class RestBusClient : HttpMessageInvoker
    {
        const string REQUEST_OPTIONS_KEY = "_RestBus_request_options";

        readonly IMessageMapper messageMapper;
        readonly ExchangeInfo exchangeInfo;
        readonly string clientId;
        readonly string exchangeName;
        readonly string callbackQueueName;
        readonly ConnectionFactory connectionFactory;
        QueueingBasicConsumer callbackConsumer = null;
        IConnection conn = null;
        event Action<BasicDeliverEventArgs> responseArrivalNotification = null;
        AmqpChannelPooler _clientPool;


        readonly object callbackConsumerStartSync = new object();
        object exchangeDeclareSync = new object();
        int lastExchangeDeclareTickCount = 0;
        volatile bool disposed = false;

        private bool hasKickStarted = false;
        private Uri baseAddress;
        private HttpRequestHeaders defaultRequestHeaders;
        private TimeSpan timeout;


        public const int HEART_BEAT = 30;

        /// <summary>Initializes a new instance of the <see cref="T:RestBus.RabbitMQ.RestBusClient" /> class.</summary>
        public RestBusClient(IMessageMapper messageMapper) : base(new HttpClientHandler(), true)
        {
            //Set default HttpClient related fields
            timeout = TimeSpan.FromSeconds(100);
            MaxResponseContentBufferSize = int.MaxValue;
            //TODO: Setup cancellation token here.

            //Configure RestBus fields/properties
            this.messageMapper = messageMapper;
            this.exchangeInfo = messageMapper.GetExchangeInfo();
            this.clientId = AmqpUtils.GetRandomId();
            this.exchangeName = AmqpUtils.GetExchangeName(exchangeInfo);
            this.callbackQueueName = AmqpUtils.GetCallbackQueueName(exchangeInfo, clientId);

            //Map request to RabbitMQ Host and exchange, 
            this.connectionFactory = new ConnectionFactory();
            connectionFactory.Uri = exchangeInfo.ServerAddress;
            connectionFactory.RequestedHeartbeat = HEART_BEAT;
        }

        /// <summary>Gets or sets the base address of Uniform Resource Identifier (URI) of the Internet resource used when sending requests.</summary>
        /// <returns>Returns <see cref="T:System.Uri" />.The base address of Uniform Resource Identifier (URI) of the Internet resource used when sending requests.</returns>
        public Uri BaseAddress
        {
            get
            {
                return baseAddress;
            }
            set
            {
                EnsureNotStartedOrDisposed();
                baseAddress = value;
            }
        }

        /// <summary>Gets the headers which should be sent with each request.</summary>
        /// <returns>Returns <see cref="T:System.Net.Http.Headers.HttpRequestHeaders" />.The headers which should be sent with each request.</returns>
        public HttpRequestHeaders DefaultRequestHeaders
        {
            //HTTPRequestHeaders ctor is internal so this property cannot be instantiated by tgis class and so is useless ...sigh...
            //Fortunately, you can specify Headers per message when using the RequestOptions class

            //TODO: Consider throwing a NotSupported Exception here instead, since a caller will not expect null.
            get
            {
                return defaultRequestHeaders;
            }
        }

        /// <summary>Gets or sets the maximum number of bytes to buffer when reading the response content.</summary>
        /// <returns>Returns <see cref="T:System.Int32" />.The maximum number of bytes to buffer when reading the response content. The default value for this property is 64K.</returns>
        /// <exception cref="T:System.ArgumentOutOfRangeException">The size specified is less than or equal to zero.</exception>
        /// <exception cref="T:System.InvalidOperationException">An operation has already been started on the current instance. </exception>
        /// <exception cref="T:System.ObjectDisposedException">The current instance has been disposed. </exception>
        public long MaxResponseContentBufferSize
        {
            //Entire Message is dequeued from queue
            //So this property is only here for compatibilty with HttpClient and does nothing
            get;
            set;
        }

        /// <summary>Gets or sets the number of milliseconds to wait before the request times out.</summary>
        /// <returns>Returns <see cref="T:System.TimeSpan" />.The number of milliseconds to wait before the request times out.</returns>
        /// <exception cref="T:System.ArgumentOutOfRangeException">The timeout specified is less than zero and is not <see cref="F:System.Threading.Timeout.Infinite" />.</exception>
        /// <exception cref="T:System.InvalidOperationException">An operation has already been started on the current instance. </exception>
        /// <exception cref="T:System.ObjectDisposedException">The current instance has been disposed.</exception>
        public TimeSpan Timeout
        {
            get
            {
                return timeout;
            }
            set
            {
                if (value != System.Threading.Timeout.InfiniteTimeSpan  && value <= TimeSpan.Zero)
                {
                    throw new ArgumentOutOfRangeException("value");
                }
                EnsureNotStartedOrDisposed();
                timeout = value;
            }
        }

        /// <summary>Cancel all pending requests on this instance.</summary>
        public void CancelPendingRequests()
        {
            //TODO: Implement CancelPendingRequests() 
        }


        //TODO: Confirm that this method is thread safe

        /// <summary>Send an HTTP request as an asynchronous operation.</summary>
        /// <returns>Returns <see cref="T:System.Threading.Tasks.Task`1" />.The task object representing the asynchronous operation.</returns>
        /// <param name="request">The HTTP request message to send.</param>
        /// <param name="cancellationToken">The cancellation token to cancel operation.</param>
        /// <exception cref="T:System.ArgumentNullException">The <paramref name="request" /> was null.</exception>
        public override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, System.Threading.CancellationToken cancellationToken)
        {
            if (request == null) throw new ArgumentNullException("request");

            if (request.RequestUri == null && BaseAddress == null )
            {
               throw new InvalidOperationException("The request URI must either be set or BaseAddress must be set");
            }

            if (disposed) throw new ObjectDisposedException("Client has been disposed");
            hasKickStarted = true;
            PrepareMessage(request);

            //Get Request Options
            RequestOptions requestOptions = GetRequestOptions(request);

            //Declare messaging resources
            Action<BasicDeliverEventArgs> arrival = null;
            ManualResetEventSlim receivedEvent = null;
            AmqpModelContainer model = null;
            bool modelClosed = false;

            try
            {
                //Start Callback consumer if it hasn't started
                StartCallbackQueueConsumer();

                var pooler = _clientPool; //Henceforth, use pooler since _clientPool may change and we want to work with the original pooler

                //TODO: test if conn or pooler is null, then leave
                if (conn == null || pooler == null)
                {
                    //TODO: This means a connection could not be created most likely because the server was Unreachable
                    //TODO: Throw some kind of HTTP 500 (Unreachable) exception
                    throw new ApplicationException("This is Bad");
                }


                //Setup message task and response arrival event notification:

                //TODO: if exchangeInfo wants a Session/Server/Sticky Queue

                string correlationId = AmqpUtils.GetRandomId();
                BasicProperties basicProperties = new BasicProperties { CorrelationId = correlationId };

                //TODO: Check if cancellation token was set before operation even began

                var taskSource = new TaskCompletionSource<HttpResponseMessage>();

                TimeSpan requestTimeout = GetRequestTimeout(requestOptions);

                if (requestTimeout != TimeSpan.Zero)
                {
                    basicProperties.ReplyTo = callbackQueueName;
                    if (!IsRequestTimeoutInfinite(requestOptions) && messageMapper.GetExpires(request))
                    {
                        if (requestTimeout.TotalMilliseconds > Int32.MaxValue)
                        {
                            basicProperties.Expiration = Int32.MaxValue.ToString();
                        }
                        else
                        {
                            basicProperties.Expiration = requestTimeout.TotalMilliseconds.ToString();
                        }
                    }

                    //Message arrival event
                    HttpResponsePacket responsePacket = null;
                    bool deserializationError = false;
                    receivedEvent = new ManualResetEventSlim(false);

                    arrival = a =>
                    {
                        if (a.BasicProperties.CorrelationId == correlationId)
                        {
                            //TODO: If deserialization failed then set exception
                            HttpResponsePacket res = null;
                            try
                            {
                                res = HttpResponsePacket.Deserialize(a.Body);
                            }
                            catch
                            {
                                deserializationError = true;
                            }

                            //Add/Update Content-Length Header
                            res.Headers["Content-Length"] = new string[]{(res.Content == null ? 0 : res.Content.Length).ToString()};;

                            if (!deserializationError)
                            {
                                responsePacket = res;
                            }
                            receivedEvent.Set();
                            responseArrivalNotification -= arrival;
                        }
                    };

                    if (!cancellationToken.Equals(System.Threading.CancellationToken.None))
                    {
                        //TODO: Have cancellationtokens cancel event trigger callbackHandle
                        //In fact turn this whole thing into an extension
                    }


                    //Create task for message arrival event
                    var localVariableInitLock = new object();

                    lock (localVariableInitLock)
                    {
                        RegisteredWaitHandle callbackHandle = null;
                        callbackHandle = ThreadPool.RegisterWaitForSingleObject(receivedEvent.WaitHandle,
                            (state, timedOut) =>
                            {
                                try
                                {
                                    //TODO: Check Cancelation Token when it's implemented
                                    if (timedOut)
                                    {
                                        //TODO: This should be a HTTP timed out exception;
                                        taskSource.SetException(new ApplicationException());
                                    }
                                    else
                                    {

                                        //TODO: How do we ensure that response (and deserializationError) is properly seen across different threads
                                        HttpResponseMessage msg;
                                        if (!deserializationError && responsePacket.TryGetHttpResponseMessage(out msg))
                                        {
                                            msg.RequestMessage = request;
                                            taskSource.SetResult(msg);
                                        }
                                        else
                                        {
                                            //TODO: This should be one that translates to a bad response message error 
                                            taskSource.SetException(new ApplicationException());
                                        }

                                    }

                                    lock (localVariableInitLock)
                                    {
                                        callbackHandle.Unregister(null);
                                    }
                                }
                                finally
                                {
                                    CleanupMessagingResources(arrival, receivedEvent);
                                }
                            },
                                null,
                                IsRequestTimeoutInfinite(requestOptions) ? -1 : (long)requestTimeout.TotalMilliseconds,
                                true);

                    }

                    responseArrivalNotification += arrival; //Confirm that this is thread-safe
                }


                //Get AMQP Model/Channel and send message

                //NOTE: Do not share channels across threads.
                //TODO: Investigate Channel pooling to see if there is significant increase in throughput on connections with reasonable latency
                model = pooler.GetModel(ChannelFlags.None);

                TimeSpan elapsedSinceLastDeclareExchange = TimeSpan.FromMilliseconds(Environment.TickCount - lastExchangeDeclareTickCount);
                if (lastExchangeDeclareTickCount == 0 || elapsedSinceLastDeclareExchange.TotalMilliseconds < 0 || elapsedSinceLastDeclareExchange.TotalSeconds > 30)
                {
                    //Redeclare exchanges and queues every 30 seconds

                    Interlocked.Exchange(ref lastExchangeDeclareTickCount, Environment.TickCount);
                    AmqpUtils.DeclareExchangeAndQueues(model.Channel, exchangeInfo, exchangeDeclareSync, null);
                }

                //Send message
                model.Channel.BasicPublish(exchangeName,
                                messageMapper.GetRoutingKey(request) ?? AmqpUtils.GetWorkQueueRoutingKey(),
                                basicProperties,
                                (new HttpRequestPacket(request)).Serialize());

                //Close channel
                CloseAmqpModel(model);
                modelClosed = true;


                if (requestTimeout == TimeSpan.Zero)
                {
                    //TODO: Investigate adding a publisher confirm for zero timeout messages so we know that RabbitMQ did pick up the message before relying OK.

                    //Zero timespan means the client isn't interested in a response
                    taskSource.SetResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new ByteArrayContent(new byte[0]) });

                    CleanupMessagingResources(arrival, receivedEvent);
                }

                //TODO: Verify that calls to Wait() on the task do not loop for change and instead rely on Kernel for notification

                return taskSource.Task;

            }
            catch
            {
                //TODO: Log this

                if (!modelClosed)
                {
                    CloseAmqpModel(model);
                }
                CleanupMessagingResources(arrival, receivedEvent);

                throw;

            }


        }

        #region xxxAsync Methods attached to the HTTP Client

        //All the xxxAsync methods should have been implemented as extension methods (all except SendAsync), oh well....

        /// <summary>Send a DELETE request to the specified Uri as an asynchronous operation.</summary>
        /// <returns>Returns <see cref="T:System.Threading.Tasks.Task`1" />.The task object representing the asynchronous operation.</returns>
        /// <param name="requestUri">The Uri the request is sent to.</param>
        /// <exception cref="T:System.ArgumentNullException">The <paramref name="requestUri" /> was null.</exception>
        public Task<HttpResponseMessage> DeleteAsync(string requestUri)
        {
            return DeleteAsync(GetUri(requestUri));
        }

        /// <summary>Send a DELETE request to the specified Uri as an asynchronous operation.</summary>
        /// <returns>Returns <see cref="T:System.Threading.Tasks.Task`1" />.The task object representing the asynchronous operation.</returns>
        /// <param name="requestUri">The Uri the request is sent to.</param>
        /// <exception cref="T:System.ArgumentNullException">The <paramref name="requestUri" /> was null.</exception>
        public Task<HttpResponseMessage> DeleteAsync(Uri requestUri)
        {
            return DeleteAsync(requestUri, CancellationToken.None);
        }

        /// <summary>Send a DELETE request to the specified Uri with a cancellation token as an asynchronous operation.</summary>
        /// <returns>Returns <see cref="T:System.Threading.Tasks.Task`1" />.The task object representing the asynchronous operation.</returns>
        /// <param name="requestUri">The Uri the request is sent to.</param>
        /// <param name="cancellationToken">A cancellation token that can be used by other objects or threads to receive notice of cancellation.</param>
        /// <exception cref="T:System.ArgumentNullException">The <paramref name="requestUri" /> was null.</exception>
        public Task<HttpResponseMessage> DeleteAsync(string requestUri, CancellationToken cancellationToken)
        {
            return DeleteAsync(GetUri(requestUri), cancellationToken);
        }

        /// <summary>Send a DELETE request to the specified Uri with a cancellation token as an asynchronous operation.</summary>
        /// <returns>Returns <see cref="T:System.Threading.Tasks.Task`1" />.The task object representing the asynchronous operation.</returns>
        /// <param name="requestUri">The Uri the request is sent to.</param>
        /// <param name="cancellationToken">A cancellation token that can be used by other objects or threads to receive notice of cancellation.</param>
        /// <exception cref="T:System.ArgumentNullException">The <paramref name="requestUri" /> was null.</exception>
        public Task<HttpResponseMessage> DeleteAsync(Uri requestUri, CancellationToken cancellationToken)
        {
            return SendAsync(new HttpRequestMessage(HttpMethod.Delete, requestUri), cancellationToken);
        }

        /// <summary>Send a GET request to the specified Uri as an asynchronous operation.</summary>
        /// <returns>Returns <see cref="T:System.Threading.Tasks.Task`1" />.The task object representing the asynchronous operation.</returns>
        /// <param name="requestUri">The Uri the request is sent to.</param>
        /// <exception cref="T:System.ArgumentNullException">The <paramref name="requestUri" /> was null.</exception>
        public Task<HttpResponseMessage> GetAsync(string requestUri)
        {
            return GetAsync(GetUri(requestUri));
        }

        /// <summary>Send a GET request to the specified Uri as an asynchronous operation.</summary>
        /// <returns>Returns <see cref="T:System.Threading.Tasks.Task`1" />.The task object representing the asynchronous operation.</returns>
        /// <param name="requestUri">The Uri the request is sent to.</param>
        /// <exception cref="T:System.ArgumentNullException">The <paramref name="requestUri" /> was null.</exception>
        public Task<HttpResponseMessage> GetAsync(Uri requestUri)
        {
            return GetAsync(requestUri, HttpCompletionOption.ResponseContentRead);
        }

        /// <summary>Send a GET request to the specified Uri with an HTTP completion option as an asynchronous operation.</summary>
        /// <returns>Returns <see cref="T:System.Threading.Tasks.Task`1" />.</returns>
        /// <param name="requestUri">The Uri the request is sent to.</param>
        /// <param name="completionOption">An HTTP completion option value that indicates when the operation should be considered completed.</param>
        /// <exception cref="T:System.ArgumentNullException">The <paramref name="requestUri" /> was null.</exception>
        public Task<HttpResponseMessage> GetAsync(string requestUri, HttpCompletionOption completionOption)
        {
            return GetAsync(GetUri(requestUri), completionOption);
        }

        /// <summary>Send a GET request to the specified Uri with an HTTP completion option as an asynchronous operation.</summary>
        /// <returns>Returns <see cref="T:System.Threading.Tasks.Task`1" />.The task object representing the asynchronous operation.</returns>
        /// <param name="requestUri">The Uri the request is sent to.</param>
        /// <param name="completionOption">An HTTP  completion option value that indicates when the operation should be considered completed.</param>
        /// <exception cref="T:System.ArgumentNullException">The <paramref name="requestUri" /> was null.</exception>
        public Task<HttpResponseMessage> GetAsync(Uri requestUri, HttpCompletionOption completionOption)
        {
            return GetAsync(requestUri, completionOption, CancellationToken.None);
        }

        /// <summary>Send a GET request to the specified Uri with a cancellation token as an asynchronous operation.</summary>
        /// <returns>Returns <see cref="T:System.Threading.Tasks.Task`1" />.</returns>
        /// <param name="requestUri">The Uri the request is sent to.</param>
        /// <param name="cancellationToken">A cancellation token that can be used by other objects or threads to receive notice of cancellation.</param>
        /// <exception cref="T:System.ArgumentNullException">The <paramref name="requestUri" /> was null.</exception>
        public Task<HttpResponseMessage> GetAsync(string requestUri, CancellationToken cancellationToken)
        {
            return GetAsync(GetUri(requestUri), cancellationToken);
        }

        /// <summary>Send a GET request to the specified Uri with a cancellation token as an asynchronous operation.</summary>
        /// <returns>Returns <see cref="T:System.Threading.Tasks.Task`1" />.The task object representing the asynchronous operation.</returns>
        /// <param name="requestUri">The Uri the request is sent to.</param>
        /// <param name="cancellationToken">A cancellation token that can be used by other objects or threads to receive notice of cancellation.</param>
        /// <exception cref="T:System.ArgumentNullException">The <paramref name="requestUri" /> was null.</exception>
        public Task<HttpResponseMessage> GetAsync(Uri requestUri, CancellationToken cancellationToken)
        {
            return GetAsync(requestUri, HttpCompletionOption.ResponseContentRead, cancellationToken);
        }

        /// <summary>Send a GET request to the specified Uri with an HTTP completion option and a cancellation token as an asynchronous operation.</summary>
        /// <returns>Returns <see cref="T:System.Threading.Tasks.Task`1" />.</returns>
        /// <param name="requestUri">The Uri the request is sent to.</param>
        /// <param name="completionOption">An HTTP  completion option value that indicates when the operation should be considered completed.</param>
        /// <param name="cancellationToken">A cancellation token that can be used by other objects or threads to receive notice of cancellation.</param>
        /// <exception cref="T:System.ArgumentNullException">The <paramref name="requestUri" /> was null.</exception>
        public Task<HttpResponseMessage> GetAsync(string requestUri, HttpCompletionOption completionOption, CancellationToken cancellationToken)
        {
            return GetAsync(GetUri(requestUri), completionOption, cancellationToken);
        }

        /// <summary>Send a GET request to the specified Uri with an HTTP completion option and a cancellation token as an asynchronous operation.</summary>
        /// <returns>Returns <see cref="T:System.Threading.Tasks.Task`1" />.The task object representing the asynchronous operation.</returns>
        /// <param name="requestUri">The Uri the request is sent to.</param>
        /// <param name="completionOption">An HTTP  completion option value that indicates when the operation should be considered completed.</param>
        /// <param name="cancellationToken">A cancellation token that can be used by other objects or threads to receive notice of cancellation.</param>
        /// <exception cref="T:System.ArgumentNullException">The <paramref name="requestUri" /> was null.</exception>
        public Task<HttpResponseMessage> GetAsync(Uri requestUri, HttpCompletionOption completionOption, CancellationToken cancellationToken)
        {
            return SendAsync(new HttpRequestMessage(HttpMethod.Get, requestUri), completionOption, cancellationToken);
        }

        /// <summary>Send a GET request to the specified Uri and return the response body as a byte array in an asynchronous operation.</summary>
        /// <returns>Returns <see cref="T:System.Threading.Tasks.Task`1" />.The task object representing the asynchronous operation.</returns>
        /// <param name="requestUri">The Uri the request is sent to.</param>
        /// <exception cref="T:System.ArgumentNullException">The <paramref name="requestUri" /> was null.</exception>
        public Task<byte[]> GetByteArrayAsync(string requestUri)
        {
            return GetByteArrayAsync(GetUri(requestUri));
        }

        /// <summary>Send a GET request to the specified Uri and return the response body as a byte array in an asynchronous operation.</summary>
        /// <returns>Returns <see cref="T:System.Threading.Tasks.Task`1" />.The task object representing the asynchronous operation.</returns>
        /// <param name="requestUri">The Uri the request is sent to.</param>
        /// <exception cref="T:System.ArgumentNullException">The <paramref name="requestUri" /> was null.</exception>
        public Task<byte[]> GetByteArrayAsync(Uri requestUri)
        {
            //TODO: Look into adding Task.ConfigureAwait (false) here
            //TODO: Test this -- Note there is a similar method in RestBusExtensions
            return SendAsync(new HttpRequestMessage(HttpMethod.Get, requestUri)).ContinueWith<byte[]>( task => task.Result.Content.ReadAsByteArrayAsync().Result);
        }

        /// <summary>Send a GET request to the specified Uri and return the response body as a stream in an asynchronous operation.</summary>
        /// <returns>Returns <see cref="T:System.Threading.Tasks.Task`1" />.The task object representing the asynchronous operation.</returns>
        /// <param name="requestUri">The Uri the request is sent to.</param>
        /// <exception cref="T:System.ArgumentNullException">The <paramref name="requestUri" /> was null.</exception>
        public Task<Stream> GetStreamAsync(string requestUri)
        {
            return GetStreamAsync(GetUri(requestUri));
        }

        /// <summary>Send a GET request to the specified Uri and return the response body as a stream in an asynchronous operation.</summary>
        /// <returns>Returns <see cref="T:System.Threading.Tasks.Task`1" />.The task object representing the asynchronous operation.</returns>
        /// <param name="requestUri">The Uri the request is sent to.</param>
        /// <exception cref="T:System.ArgumentNullException">The <paramref name="requestUri" /> was null.</exception>
        public Task<Stream> GetStreamAsync(Uri requestUri)
        {
            //TODO: Look into adding Task.ConfigureAwait (false) here
            //TODO: Test this -- Note there is a similar method in RestBusExtensions
            return SendAsync(new HttpRequestMessage(HttpMethod.Get, requestUri)).ContinueWith<Stream>(task => task.Result.Content.ReadAsStreamAsync().Result);
        }

        /// <summary>Send a GET request to the specified Uri and return the response body as a string in an asynchronous operation.</summary>
        /// <returns>Returns <see cref="T:System.Threading.Tasks.Task`1" />.The task object representing the asynchronous operation.</returns>
        /// <param name="requestUri">The Uri the request is sent to.</param>
        /// <exception cref="T:System.ArgumentNullException">The <paramref name="requestUri" /> was null.</exception>
        public Task<string> GetStringAsync(string requestUri)
        {
            return GetStringAsync(GetUri(requestUri));
        }

        /// <summary>Send a GET request to the specified Uri and return the response body as a string in an asynchronous operation.</summary>
        /// <returns>Returns <see cref="T:System.Threading.Tasks.Task`1" />.The task object representing the asynchronous operation.</returns>
        /// <param name="requestUri">The Uri the request is sent to.</param>
        /// <exception cref="T:System.ArgumentNullException">The <paramref name="requestUri" /> was null.</exception>
        public Task<string> GetStringAsync(Uri requestUri)
        {
            //TODO: Look into adding Task.ConfigureAwait (false) here
            //TODO: Test this -- Note there is a similar method in RestBusExtensions
            return SendAsync(new HttpRequestMessage(HttpMethod.Get, requestUri)).ContinueWith<string>(task => task.Result.Content.ReadAsStringAsync().Result);
        }

        /// <summary>Send a POST request to the specified Uri as an asynchronous operation.</summary>
        /// <returns>Returns <see cref="T:System.Threading.Tasks.Task`1" />.The task object representing the asynchronous operation.</returns>
        /// <param name="requestUri">The Uri the request is sent to.</param>
        /// <param name="content">The HTTP request content sent to the server.</param>
        /// <exception cref="T:System.ArgumentNullException">The <paramref name="requestUri" /> was null.</exception>
        public Task<HttpResponseMessage> PostAsync(string requestUri, HttpContent content)
        {
            return PostAsync(GetUri(requestUri), content);
        }

        /// <summary>Send a POST request to the specified Uri as an asynchronous operation.</summary>
        /// <returns>Returns <see cref="T:System.Threading.Tasks.Task`1" />.The task object representing the asynchronous operation.</returns>
        /// <param name="requestUri">The Uri the request is sent to.</param>
        /// <param name="content">The HTTP request content sent to the server.</param>
        /// <exception cref="T:System.ArgumentNullException">The <paramref name="requestUri" /> was null.</exception>
        public Task<HttpResponseMessage> PostAsync(Uri requestUri, HttpContent content)
        {
            return PostAsync(requestUri, content, CancellationToken.None);
        }

        /// <summary>Send a POST request with a cancellation token as an asynchronous operation.</summary>
        /// <returns>Returns <see cref="T:System.Threading.Tasks.Task`1" />.The task object representing the asynchronous operation.</returns>
        /// <param name="requestUri">The Uri the request is sent to.</param>
        /// <param name="content">The HTTP request content sent to the server.</param>
        /// <param name="cancellationToken">A cancellation token that can be used by other objects or threads to receive notice of cancellation.</param>
        /// <exception cref="T:System.ArgumentNullException">The <paramref name="requestUri" /> was null.</exception>
        public Task<HttpResponseMessage> PostAsync(string requestUri, HttpContent content, CancellationToken cancellationToken)
        {
            return PostAsync(GetUri(requestUri), content, cancellationToken);
        }

        /// <summary>Send a POST request with a cancellation token as an asynchronous operation.</summary>
        /// <returns>Returns <see cref="T:System.Threading.Tasks.Task`1" />.The task object representing the asynchronous operation.</returns>
        /// <param name="requestUri">The Uri the request is sent to.</param>
        /// <param name="content">The HTTP request content sent to the server.</param>
        /// <param name="cancellationToken">A cancellation token that can be used by other objects or threads to receive notice of cancellation.</param>
        /// <exception cref="T:System.ArgumentNullException">The <paramref name="requestUri" /> was null.</exception>
        public Task<HttpResponseMessage> PostAsync(Uri requestUri, HttpContent content, CancellationToken cancellationToken)
        {
            return SendAsync(new HttpRequestMessage(HttpMethod.Post, requestUri)
            {
                Content = content
            }, cancellationToken);
        }

        /// <summary>Send a PUT request to the specified Uri as an asynchronous operation.</summary>
        /// <returns>Returns <see cref="T:System.Threading.Tasks.Task`1" />.The task object representing the asynchronous operation.</returns>
        /// <param name="requestUri">The Uri the request is sent to.</param>
        /// <param name="content">The HTTP request content sent to the server.</param>
        /// <exception cref="T:System.ArgumentNullException">The <paramref name="requestUri" /> was null.</exception>
        public Task<HttpResponseMessage> PutAsync(string requestUri, HttpContent content)
        {
            return PutAsync(GetUri(requestUri), content);
        }

        /// <summary>Send a PUT request to the specified Uri as an asynchronous operation.</summary>
        /// <returns>Returns <see cref="T:System.Threading.Tasks.Task`1" />.The task object representing the asynchronous operation.</returns>
        /// <param name="requestUri">The Uri the request is sent to.</param>
        /// <param name="content">The HTTP request content sent to the server.</param>
        /// <exception cref="T:System.ArgumentNullException">The <paramref name="requestUri" /> was null.</exception>
        public Task<HttpResponseMessage> PutAsync(Uri requestUri, HttpContent content)
        {
            return PutAsync(requestUri, content, CancellationToken.None);
        }

        /// <summary>Send a PUT request with a cancellation token as an asynchronous operation.</summary>
        /// <returns>Returns <see cref="T:System.Threading.Tasks.Task`1" />.The task object representing the asynchronous operation.</returns>
        /// <param name="requestUri">The Uri the request is sent to.</param>
        /// <param name="content">The HTTP request content sent to the server.</param>
        /// <param name="cancellationToken">A cancellation token that can be used by other objects or threads to receive notice of cancellation.</param>
        /// <exception cref="T:System.ArgumentNullException">The <paramref name="requestUri" /> was null.</exception>
        public Task<HttpResponseMessage> PutAsync(string requestUri, HttpContent content, CancellationToken cancellationToken)
        {
            return PutAsync(GetUri(requestUri), content, cancellationToken);
        }

        /// <summary>Send a PUT request with a cancellation token as an asynchronous operation.</summary>
        /// <returns>Returns <see cref="T:System.Threading.Tasks.Task`1" />.The task object representing the asynchronous operation.</returns>
        /// <param name="requestUri">The Uri the request is sent to.</param>
        /// <param name="content">The HTTP request content sent to the server.</param>
        /// <param name="cancellationToken">A cancellation token that can be used by other objects or threads to receive notice of cancellation.</param>
        /// <exception cref="T:System.ArgumentNullException">The <paramref name="requestUri" /> was null.</exception>
        public Task<HttpResponseMessage> PutAsync(Uri requestUri, HttpContent content, CancellationToken cancellationToken)
        {
            return SendAsync(new HttpRequestMessage(HttpMethod.Put, requestUri)
            {
                Content = content
            }, cancellationToken);
        }

        /// <summary>Send an HTTP request as an asynchronous operation.</summary>
        /// <returns>Returns <see cref="T:System.Threading.Tasks.Task`1" />.The task object representing the asynchronous operation.</returns>
        /// <param name="request">The HTTP request message to send.</param>
        /// <exception cref="T:System.ArgumentNullException">The <paramref name="request" /> was null.</exception>
        public Task<HttpResponseMessage> SendAsync(HttpRequestMessage request)
        {
            return SendAsync(request, HttpCompletionOption.ResponseContentRead, CancellationToken.None);
        }

        /// <summary>Send an HTTP request as an asynchronous operation.</summary>
        /// <returns>Returns <see cref="T:System.Threading.Tasks.Task`1" />.The task object representing the asynchronous operation.</returns>
        /// <param name="request">The HTTP request message to send.</param>
        /// <param name="completionOption">When the operation should complete (as soon as a response is available or after reading the whole response content).</param>
        /// <exception cref="T:System.ArgumentNullException">The <paramref name="request" /> was null.</exception>
        public Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, HttpCompletionOption completionOption)
        {
            return SendAsync(request, completionOption, CancellationToken.None);
        }

        /// <summary>Send an HTTP request as an asynchronous operation. </summary>
        /// <returns>Returns <see cref="T:System.Threading.Tasks.Task`1" />.The task object representing the asynchronous operation.</returns>
        /// <param name="request">The HTTP request message to send.</param>
        /// <param name="completionOption">When the operation should complete (as soon as a response is available or after reading the whole response content).</param>
        /// <exception cref="T:System.ArgumentNullException">The <paramref name="request" /> was null.</exception>
        /// <param name="cancellationToken">A cancellation token that can be used by other objects or threads to receive notice of cancellation.</param>
        public Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, HttpCompletionOption completionOption, CancellationToken cancellationToken)
        {
            return SendAsync(request, cancellationToken);
        }


        #endregion

        internal static RequestOptions GetRequestOptions(HttpRequestMessage request)
        {
            object reqObj;
            if (request.Properties.TryGetValue(REQUEST_OPTIONS_KEY, out reqObj))
            {
                return reqObj as RequestOptions;
            }

            return null;
        }

        internal static void SetRequestOptions(HttpRequestMessage request, RequestOptions options)
        {
            request.Properties[REQUEST_OPTIONS_KEY] = options;
        }

        protected override void Dispose(bool disposing)
        {

            //TODO: Work on this method

            //TODO: Confirm that this does in fact kill all background threads

            disposed = true;
            DisposeConnection();

            if (_clientPool != null) _clientPool.Dispose();

            //TODO: Kill all channels in channel pool (if implemented)

            base.Dispose(disposing);

        }

        private void CloseAmqpModel(AmqpModelContainer model)
        {
            if (model != null)
            {
                model.Close();
            }
        }

        private void CleanupMessagingResources(Action<BasicDeliverEventArgs> arrival, ManualResetEventSlim receivedEvent)
        {
            if (arrival != null)
            {
                try
                {
                    //TODO: Investigate if removing/adding delegates is threadsafe.
                    responseArrivalNotification -= arrival;
                }
                catch { }
            }

            if (receivedEvent != null)
            {
                receivedEvent.Dispose();
            }
        }

        private TimeSpan GetRequestTimeout(RequestOptions options)
        {
            return GetTimeoutValue(options).Duration();
        }

        private bool IsRequestTimeoutInfinite(RequestOptions options)
        {
            return GetTimeoutValue(options) == System.Threading.Timeout.InfiniteTimeSpan; //new TimeSpan(0, 0, 0, 0, -1)
        }

        private TimeSpan GetTimeoutValue(RequestOptions options)
        {
            TimeSpan timeoutVal = this.Timeout;

            if (options != null && options.Timeout.HasValue)
            {
                timeoutVal = options.Timeout.Value;
            }
            return timeoutVal;
        }

        private Uri GetUri(string uri)
        {
            if (string.IsNullOrEmpty(uri)) return null;
            return new Uri(uri, UriKind.RelativeOrAbsolute);
        }

        private void StartCallbackQueueConsumer()
        {
            //TODO: Double-checked locking -- make this better
            //TODO: Consider moving the conn related checks into a pooler method
            if (callbackConsumer == null || conn == null || !callbackConsumer.IsRunning || !conn.IsOpen)
            {
                lock (callbackConsumerStartSync)
                {
                    if (!(callbackConsumer == null || conn == null || !callbackConsumer.IsRunning || !conn.IsOpen)) return;

                    //This method waits on this signal to make sure the callbackprocessor thread either started successfully or failed.
                    ManualResetEventSlim consumerSignal = new ManualResetEventSlim(false);

                    Thread callBackProcessor = new Thread(p =>
                    {

                        try
                        {

                            //NOTE: This is the only place where connections are created in the client
                            //NOTE: CreateConnection() can always throw RabbitMQ.Client.Exceptions.BrokerUnreachableException
                            conn = connectionFactory.CreateConnection();

                            AmqpChannelPooler oldpool = _clientPool;

                            //TODO: Is it necessary to do a CompareExchange since this is within a lock? //Yes, cos this is in a brand new thread
                            _clientPool = new AmqpChannelPooler(conn);

                            //Dispose old pool -- after making sure new pool was assigned.
                            if (oldpool != null)
                            {
                                oldpool.Dispose();
                            }

                            using (IModel channel = conn.CreateModel())
                            {
                                //Declare call back queue
                                var callbackQueueArgs = new System.Collections.Hashtable();
                                callbackQueueArgs.Add("x-expires", (long)AmqpUtils.GetCallbackQueueExpiry().TotalMilliseconds);

                                channel.QueueDeclare(callbackQueueName, false, false, true, callbackQueueArgs);

                                callbackConsumer = new QueueingBasicConsumer(channel);
                                channel.BasicConsume(callbackQueueName, false, callbackConsumer);

                                //Notify outer thread that channel has started consumption
                                consumerSignal.Set();

                                object obj;
                                BasicDeliverEventArgs evt;

                                while (true)
                                {

                                    try
                                    {
                                        obj = DequeueCallbackQueue();
                                    }
                                    catch
                                    {
                                        //TODO: Log this exception except it's ObjectDisposedException
                                        throw;
                                    }

                                    evt = (BasicDeliverEventArgs)obj;

                                    try
                                    {
                                        if (responseArrivalNotification != null)
                                        {
                                            responseArrivalNotification(evt);
                                        }
                                    }
                                    catch
                                    {
                                        //DO nothing
                                    }

                                    //Acknowledge receipt
                                    channel.BasicAck(evt.DeliveryTag, false);

                                }

                            }
                        }
                        catch
                        {
                            //TODO: Log error (Except it's object disposed exception)
                            //TODO: Set Exception object which will be throw by signal waiter

                            //Notify outer thread to move on, in case it's still waiting
                            try
                            {
                                consumerSignal.Set();
                            }
                            catch { }


                        }
                        finally
                        {
                            if (_clientPool != null)
                            {
                                _clientPool.Dispose();
                            }
                            DisposeConnection();
                        }

                    });

                    //Start Thread
                    callBackProcessor.Name = "RestBus RabbitMQ Client Callback Queue Consumer";
                    callBackProcessor.IsBackground = true;
                    callBackProcessor.Start();

                    //Wait for Thread to start consuming messages
                    consumerSignal.Wait();

                    //TODO: Examine exception if it were set and rethrow it

                }
            }

        }

        private void EnsureNotStartedOrDisposed()
        {
            if (disposed) throw new ObjectDisposedException(GetType().FullName);
            if (hasKickStarted) throw new InvalidOperationException("This instance has already started one or more requests. Properties can only be modified before sending the first request.");
        }

        private void PrepareMessage(HttpRequestMessage request)
        {
            //Combine RequestUri with BaseRequest
            if (request.RequestUri == null)
            {
                request.RequestUri = this.BaseAddress;
            }
            else if (!request.RequestUri.IsAbsoluteUri)
            {
                if (this.BaseAddress != null)
                {
                    request.RequestUri = new Uri(this.BaseAddress, request.RequestUri);
                }
            }

            //Append default request headers
            if (this.DefaultRequestHeaders != null)
            {
                foreach (var header in this.DefaultRequestHeaders)
                {
                    if (!request.Headers.Contains(header.Key))
                    {
                        request.Headers.Add(header.Key, header.Value);
                    }
                }
            }

        }

        private void DisposeConnection()
        {
            if (conn != null)
            {

                try
                {
                    conn.Close();
                }
                catch
                {
                    //TODO: Log Error
                }

                try
                {
                    conn.Dispose();
                }
                catch
                {
                    //TODO: Log Error
                }
            }
        }

        private object DequeueCallbackQueue()
        {
            while (true)
            {
                if (disposed) throw new ObjectDisposedException("Client has been disposed");

                object obj = callbackConsumer.Queue.DequeueNoWait(null);

                if (obj != null)
                {
                    return obj;
                }

                Thread.Sleep(1);
            }
        }

    }



}
