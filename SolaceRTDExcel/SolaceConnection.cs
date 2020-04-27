using System;
using System.Collections.Generic;
using System.Configuration;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Threading.Tasks.Dataflow;
using NLog;
using SolaceRTDExcel.Json;
using SolaceSystems.Solclient.Messaging;

namespace SolaceRTDExcel
{
    public class SolaceConnection
    {
        private static NLog.Logger logger = NLog.LogManager.GetCurrentClassLogger();

        /// <summary>
        ///     Object used for synchronizations.
        /// </summary>
        private static readonly object _syncLock = new Object();

        private bool isInitialized = false;
        private volatile ConnectionState connectionState = ConnectionState.Created;

        private IContext context = null;
        private ISession session = null;

        // We use the TPL DataFlow Library's BufferBlocks to pass messages & events
        // from this wrapper api (producer) to the calling app (consumer)
        BufferBlock<SolaceMessage> defaultAppMsgQueue = null;
        List<BufferBlock<ConnectionEvent>> connectionEvtObservers = new List<BufferBlock<ConnectionEvent>>();
        // Task completion source for asynchronously waiting for the connection UP event
        TaskCompletionSource<ConnectionEvent> tcsConnection = null;

        // For keeping track of unique topic subscriptions
        Dictionary<string, IDispatchTarget> topicSubscriptions = new Dictionary<string, IDispatchTarget>();

        private MessageConvertor msgConvertor;
        private MessageConvertorFactory<JsonMessageConvertor> msgConvertorFactory;

        #region Configuration
        private string host;
        private string username;
        private string password;
        private string messageVpn;
        private string clientName;
        private int reconnectRetries;
        private int connectRetries;
        private int connectRetriesPerHost;
        private int reconnectRetriesWaitInMsecs;
        private bool reapplySubscription;
        private string apiLogLevel;
        #endregion

        public SolaceConnection(BufferBlock<SolaceMessage> defaultAppMsgQueue)
        {
            this.defaultAppMsgQueue = defaultAppMsgQueue;

            // Configuration 
            ReadConfiguration();

            // Initialize the API if not already done so
            lock (_syncLock)
            {
                if (!isInitialized)
                {
                    InitializeSolaceAPI(apiLogLevel);
                }
            }

            // Create the message convertor - in our demo we send JSON message payloads
            msgConvertorFactory = new JsonMessageConvertorFactory();
            msgConvertor = msgConvertorFactory.CreateConvertor();
        }

        /// <summary>
        /// Registers connection event observers.
        /// </summary>
        /// <param name="connectionEvtQueue"></param>
        public void RegisterConnectionEvents(BufferBlock<ConnectionEvent> connectionEvtQueue)
        {
            connectionEvtObservers.Add(connectionEvtQueue);
        }

        /// <summary>
        /// Asynchronously stablishes a connection to the Solace broker.
        /// Connection UP and Fail events are sent asynchronously via the BufferBlock
        /// configured for the connection.
        /// </summary>
        public Task<ConnectionEvent> ConnectAsync()
        {
            // Ignore & return if already connected
            if (connectionState == ConnectionState.Opened || connectionState == ConnectionState.Opening)
            {
                throw new MessagingException("Connection already opened or opening.");
            }

            return ConnectAsyncInternal();
        }

        private async Task<ConnectionEvent> ConnectAsyncInternal()
        {
            connectionState = ConnectionState.Opening;

            ConnectionEvent connectionEvent;
            tcsConnection = new TaskCompletionSource<ConnectionEvent>();

            // Create a new context
            var cp = new ContextProperties();
            context = ContextFactory.Instance.CreateContext(cp, null);

            // Ensure the connection & publish is done in a non-blocking fashion
            var sessionProps = GetSessionProperties();
            sessionProps.ConnectBlocking = false;
            sessionProps.SendBlocking = false;
            // Required for internal topic dispatching
            sessionProps.TopicDispatch = true;

            // Create the session with the event handlers
            session = context.CreateSession(sessionProps, MessageEventHandler, SessionEventHandler);

            // Connect the session - non-blocking
            var returnCode = session.Connect();
            if (returnCode == ReturnCode.SOLCLIENT_FAIL)
            {
                // Something bad happened before the connection attempt to Solace
                // broker
                var errorInfo = ContextFactory.Instance.GetLastSDKErrorInfo();
                throw new MessagingException(errorInfo.ErrorStr);
            }
            else
            {
                connectionEvent = await tcsConnection.Task.ConfigureAwait(false);
            }

            return connectionEvent;
        }

        /// <summary>
        /// Asynchronously disconnects from the Solace broker. 
        /// </summary>
        public Task DisconnectAsync()
        {
            if (connectionState == ConnectionState.Closed)
                return Task.FromResult(0);

            return DisconnectAsyncInternal();
        }

        private async Task DisconnectAsyncInternal()
        {
            try
            {
                await OnStateChangedAsync(ConnectionState.Closing, null).ConfigureAwait(false);
                if (session != null)
                    session.Disconnect();
                if (context != null)
                    context.Dispose();
                await OnStateChangedAsync(ConnectionState.Closed, null).ConfigureAwait(false);

                topicSubscriptions.Clear();
            }
            catch (Exception ex)
            {
                throw new MessagingException(ex.Message, ex);
            }
        }

        /// <summary>
        /// Subscribes to the destination on the Solace Event Broker asynchronously.
        /// <br/>
        /// Future: support will be added for subscribing from queues.
        /// </summary>
        /// <param name="topic">A Topic or Queue destination</param>
        /// <returns></returns>
        public Task<bool> SubscribeAsync(string topic)
        {
            if (string.IsNullOrEmpty(topic))
                throw new ArgumentNullException(nameof(topic), "Destination cannot be null");

            return SubscribeTopicAsyncInternal(topic);
        }

        private async Task<bool> SubscribeTopicAsyncInternal(string topic)
        {
            // Ignore if we have already subscribed to the topic before
            if (topicSubscriptions.ContainsKey(topic))
                return true;

            try
            {
                var solTopic = ContextFactory.Instance.CreateTopic(topic);
                TaskCompletionSource<SessionEventArgs> tcs = new TaskCompletionSource<SessionEventArgs>();
                IDispatchTarget dTarget = session.CreateDispatchTarget(solTopic,
                    async (sender, msgEv) => await AcceptMessageEventAsync(msgEv).ConfigureAwait(false));
                topicSubscriptions.Add(topic, dTarget);
                session.Subscribe(dTarget, SubscribeFlag.RequestConfirm, tcs);

                // Check subscription result
                var result = await tcs.Task.ConfigureAwait(false);
                if (result.Event == SessionEvent.SubscriptionOk)
                    return true;
                else
                {
                    logger.Error("Subscription error to topic: {0} responseCode {1} errorInfo: {2}",
                        topic, result.ResponseCode, result.Info);
                    return false;
                }
            }
            catch (Exception e)
            {
                throw new MessagingException(e.Message, e);
            }
        }

        /// <summary>
        /// Unsubscribes from the destination asynchronously.
        /// </summary>
        /// <param name="topic">The topic destination to unsubscribe.</param>
        /// <returns>True if the subscription was removed succussfully, false otherwise.</returns>
        public async Task<bool> UnsubscribeAsync(string topic)
        {
            if (!topicSubscriptions.ContainsKey(topic))
                return true;

            var dTarget = topicSubscriptions[topic];
            if (dTarget != null)
            {
                try
                {
                    TaskCompletionSource<SessionEventArgs> tcs = new TaskCompletionSource<SessionEventArgs>();
                    session.Unsubscribe(dTarget, SubscribeFlag.RequestConfirm, tcs);
                    // Check unsubscribe result
                    var result = await tcs.Task.ConfigureAwait(false);
                    // Solace API use Subscription OK events for both subscribe and unsubscribe
                    if (result.Event == SessionEvent.SubscriptionOk)
                    {
                        topicSubscriptions.Remove(topic);
                        return true;
                    }
                    else
                    {
                        logger.Error("Unsubscribe error to topic: {0} responseCode {1} errorInfo: {2}",
                            topic, result.ResponseCode, result.Info);
                        return false;
                    }
                }
                catch (Exception e)
                {
                    throw new MessagingException(e.Message, e);
                }
            }
            return true;
        }

        #region EventHandlers
        private async void SessionEventHandler(object sender, SessionEventArgs e)
        {
            switch (e.Event)
            {
                case SessionEvent.ConnectFailedError:
                case SessionEvent.DownError:
                    // Change state
                    await OnStateChangedAsync(ConnectionState.Closed, e).ConfigureAwait(false);
                    break;
                case SessionEvent.Reconnecting:
                    await OnStateChangedAsync(ConnectionState.Reconnecting, e).ConfigureAwait(false);
                    break;
                case SessionEvent.Reconnected:
                    await OnStateChangedAsync(ConnectionState.Reconnected, e).ConfigureAwait(false);
                    break;
                case SessionEvent.UpNotice:
                    await OnStateChangedAsync(ConnectionState.Opened, e).ConfigureAwait(false);
                    break;
                case SessionEvent.SubscriptionOk:
                case SessionEvent.SubscriptionError:
                    var tcs = (e.CorrelationKey as TaskCompletionSource<SessionEventArgs>);
                    tcs.TrySetResult(e);
                    break;
            }
        }

        private async void MessageEventHandler(object sender, MessageEventArgs msgEvtArgs)
        {
            await AcceptMessageEventAsync(msgEvtArgs).ConfigureAwait(false);
        }

        private async Task AcceptMessageEventAsync(MessageEventArgs msgEvtArgs)
        {
            try
            {
                var convertedMessage = msgConvertor.ConvertMessage(msgEvtArgs.Message);
                await defaultAppMsgQueue.SendAsync(convertedMessage).ConfigureAwait(false);
            }
            finally
            {
                msgEvtArgs.Message.Dispose();
            }
        }

        private async Task OnStateChangedAsync(ConnectionState state, SessionEventArgs sessionEvtArgs)
        {
            connectionState = state;
            var connectionEvent = new ConnectionEvent()
            {
                State = connectionState,
                Info = sessionEvtArgs?.Info,
                ResponseCode = sessionEvtArgs?.ResponseCode ?? 0
            };

            if (tcsConnection != null)
                tcsConnection.TrySetResult(connectionEvent);

            // Notify observers if registered
            foreach (var observer in connectionEvtObservers)
                await observer.SendAsync(connectionEvent).ConfigureAwait(false);
        }
        #endregion

        private void ReadConfiguration()
        {
            //Open the configuration file using the dll location
            Configuration dllConfig = ConfigurationManager.OpenExeConfiguration(this.GetType().Assembly.Location);
            // Get the appSettings section
            AppSettingsSection appSettings = (AppSettingsSection)dllConfig.GetSection("appSettings");
            host = appSettings.Settings["host"].Value;
            username = appSettings.Settings["username"].Value;
            password = appSettings.Settings["password"].Value;
            messageVpn = appSettings.Settings["messageVpn"].Value;
            clientName = appSettings.Settings["clientName"].Value;
            reconnectRetries = Int32.Parse(appSettings.Settings["reconnectRetries"].Value);
            connectRetries = Int32.Parse(appSettings.Settings["connectRetries"].Value);
            connectRetriesPerHost = Int32.Parse(appSettings.Settings["connectRetriesPerHost"].Value);
            reconnectRetriesWaitInMsecs = Int32.Parse(appSettings.Settings["reconnectRetriesWaitInMsecs"].Value);
            reapplySubscription = bool.Parse(appSettings.Settings["reapplySubscription"].Value);
            apiLogLevel = appSettings.Settings["apiLogLevel"].Value;
        }

        private SessionProperties GetSessionProperties()
        {
            var sessionProps = new SessionProperties();
            sessionProps.Host = host;
            sessionProps.UserName = username;
            sessionProps.Password = password;
            sessionProps.VPNName = messageVpn;
            sessionProps.ClientName = clientName;
            sessionProps.ReconnectRetries = reconnectRetries;
            sessionProps.ConnectRetries = connectRetries;
            sessionProps.ConnectRetriesPerHost = connectRetriesPerHost;
            sessionProps.ReconnectRetriesWaitInMsecs = connectRetriesPerHost;
            sessionProps.ReapplySubscriptions = reapplySubscription;
            // Connect Async
            sessionProps.ConnectBlocking = false;
            // Subscribe Async
            sessionProps.SubscribeBlocking = false;
            return sessionProps;
        }

        private void InitializeSolaceAPI(string logLevel)
        {
            // Initialize the API & set API logging
            var cfp = new ContextFactoryProperties();
            // Set log level
            cfp.SolClientLogLevel = GetSolaceLogLevel(logLevel);
            // Delegate logs to the wrapper's logging factory
            cfp.LogDelegate += OnSolaceApiLog;
            // Must init the API before using any of its artifacts.
            try
            {
                ContextFactory.Instance.Init(cfp);
            }
            catch (FatalErrorException ex)
            {
                logger.Error(ex, ex.Message);
                throw new MessagingException("Failed to Initial Solace API", ex);
            }
        }

        /// <summary>
        /// Log delegate for redirecting Solace .NET API logs to the wrapper's
        /// logging abstraction.
        /// </summary>
        /// <param name="solLogInfo">The Solace API log info containing the level, 
        /// exception, and message.</param>
        private void OnSolaceApiLog(SolLogInfo solLogInfo)
        {
            var logLevel = GetLogLevel(solLogInfo.LogLevel);
            if (logger.IsEnabled(logLevel))
            {
                logger.Log(logLevel, solLogInfo.LogException, solLogInfo.LogMessage);
            }
        }

        private SolLogLevel GetSolaceLogLevel(string logLevel)
        {
            try
            {
                return (SolLogLevel)Enum.Parse(typeof(SolLogLevel), logLevel);
            }
            catch (Exception ex)
            {
                logger.Error(ex, "Invalid Solace API log level specified - Defaulting level to NOTICE");
                return SolLogLevel.Notice;
            }
        }

        private LogLevel GetLogLevel(SolLogLevel solLogLevel)
        {
            switch (solLogLevel)
            {
                case SolLogLevel.Critical:
                    return LogLevel.Fatal;
                case SolLogLevel.Error:
                    return LogLevel.Error;
                case SolLogLevel.Warning:
                    return LogLevel.Warn;
                case SolLogLevel.Notice:
                    return LogLevel.Info;
                case SolLogLevel.Info:
                    return LogLevel.Trace;
                case SolLogLevel.Debug:
                    return LogLevel.Debug;
                case SolLogLevel.Emergency:
                case SolLogLevel.Alert:
                default:
                    return LogLevel.Off;
            }
        }
    }
}
