using Microsoft.Office.Interop.Excel;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Threading.Tasks.Dataflow;
using System.Timers;

namespace SolaceRTDExcel
{
    [Guid("63FFB811-5E79-4418-9F11-9635A52294A3")]
    [ProgId("Solace.RTD")]
    [ComVisible(true)]
    public class RTDSolaceServer : IRtdServer
    {
        private static NLog.Logger logger = NLog.LogManager.GetCurrentClassLogger();

        BufferBlock<SolaceMessage> messageQueue;
        BufferBlock<ConnectionEvent> connectionEvtQueue;
        SolaceConnection solaceConnection;
        private Dictionary<int, TopicData> topicDatas;
        private Dictionary<string, int> solaceTopics;
        private ConcurrentDictionary<string, SolaceMessage> messageCache;

        // Status codes
        private const int SUCCESS = 1;
        private const int FAILURE = 0;
        private static string NOT_CONNECTED_STATUS = "#NOT_CONNECTED!";
        private static string ERROR_STATUS = "#ERROR!";

        private volatile bool isSolaceConnected = false; 
        
        private IRTDUpdateEvent callback;
        private Timer timer;

        private class TopicData
        {
            public string SolaceTopic { get; set; }
            public string FieldName { get; set; }

            public override string ToString()
            {
                return string.Format("TopicData - [SolaceTopic: {0}, FieldName: {1}]",
                    SolaceTopic, FieldName);
            }
        }

        public RTDSolaceServer()
        {
            //Open the logging configuration file using the dll location
            try
            {
                var location = this.GetType().Assembly.Location;
                location = location.Remove(location.LastIndexOf("\\"));
                NLog.LogManager.Configuration = new NLog.Config.XmlLoggingConfiguration(location + "\\NLog.config");
            }
            catch(Exception)
            {
                // TODO: Log exception somewhere.
            }

            messageQueue = new BufferBlock<SolaceMessage>();
            connectionEvtQueue = new BufferBlock<ConnectionEvent>();
            solaceConnection = new SolaceConnection(messageQueue);
            topicDatas = new Dictionary<int, TopicData>();
            solaceTopics = new Dictionary<string, int>();
            messageCache = new ConcurrentDictionary<string, SolaceMessage>();
        }

        public int ServerStart(IRTDUpdateEvent CallbackObject)
        {
            if (isSolaceConnected)
            {
                logger.Info("Solace RTD Server already connected to Solace.");
                return SUCCESS;
            }

            try
            {
                callback = CallbackObject;
                timer = new Timer();
                timer.Elapsed += new ElapsedEventHandler(TimeEventHandler);
                timer.Interval = 1000;
                timer.Start();

                // Register to receive Solace Connection Events
                var printEvents = new ActionBlock<ConnectionEvent>(evt =>
                {
                    logger.Info("Connection Event: {0} ResponseCode: {1} Info: {2}",
                       evt.State, evt.ResponseCode, evt.Info);
                });
                connectionEvtQueue.LinkTo(printEvents);
                solaceConnection.RegisterConnectionEvents(connectionEvtQueue);

                var cacheEvents = new ActionBlock<SolaceMessage>(evt =>
                {
                    if (messageCache.ContainsKey(evt.Destination))
                        messageCache[evt.Destination] = evt;
                    else
                        messageCache.TryAdd(evt.Destination, evt);
                });
                messageQueue.LinkTo(cacheEvents);

                // Connect to Solace
                Task<ConnectionEvent> connTask = Task.Run<ConnectionEvent>(async
                    () => await solaceConnection.ConnectAsync().ConfigureAwait(false));
                var connResult = connTask.Result;
                if (connResult.State != ConnectionState.Opened)
                    return FAILURE;
                else
                    isSolaceConnected = true;

                logger.Info("Solace RTD Excel server has started successfully");
                return SUCCESS;
            }
            catch (Exception ex)
            {
                logger.Error(ex, "Exception on ServerStart()");
            }
            return FAILURE;
        }

        public dynamic ConnectData(int TopicID, ref Array Strings, ref bool GetNewValues)
        {
            if (Strings == null || Strings.Length != 2)
                return ERROR_STATUS;

            try
            {
                string solaceTopic = Strings.GetValue(0).ToString();
                string fieldName = Strings.GetValue(1).ToString();
                if (string.IsNullOrWhiteSpace(solaceTopic) || string.IsNullOrWhiteSpace(fieldName))
                {
                    logger.Error("Required Solace topic or fieldname parameter missing for Solace RTD function");
                    return ERROR_STATUS;
                }

                var topicData = new TopicData { SolaceTopic = solaceTopic, FieldName = fieldName };

                // Add the solace topic to the list of data we need to refresh
                if (!topicDatas.ContainsKey(TopicID))
                {
                    logger.Debug("Adding topic data - " + topicData.ToString());
                    topicDatas.Add(TopicID, topicData);
                }

                if (solaceTopics.ContainsKey(topicData.SolaceTopic))
                {
                    // We are already subscribed to the solace topic
                    // We are also keeping track of how many time we are requesting data from the
                    // same topic, so we can unsubscribe to the topic when reference is zero.
                    solaceTopics[topicData.SolaceTopic] += 1;
                    return GetData(topicData);
                }
                else
                {
                    return SubscribeData(topicData);
                }
            }
            catch (Exception ex)
            {
                logger.Error(ex, "Exception on ServerStart()");
            }
            return ERROR_STATUS;
        }

        public Array RefreshData(ref int TopicCount)
        { 
            TopicCount = 0;
            object[,] data = new object[2, topicDatas.Count];

            try
            {
                foreach (int topicID in topicDatas.Keys)
                {
                    var topicData = topicDatas[topicID];
                    if (!solaceTopics.ContainsKey(topicData.SolaceTopic))
                    {
                        // Subscribe to the symbol
                        SubscribeData(topicData);
                    }
                    data[0, TopicCount] = topicID;
                    data[1, TopicCount] = GetData(topicData);
                    TopicCount++;
                }
            }
            catch (Exception ex)
            {
                logger.Error(ex, "Exception on RefreshData()");
            }
            return data;
        }

        public void DisconnectData(int TopicID)
        {
            try
            {
                if (topicDatas.ContainsKey(TopicID))
                {
                    var topicData = topicDatas[TopicID];
                    topicDatas.Remove(TopicID);

                    if (solaceTopics.ContainsKey(topicData.SolaceTopic))
                    {
                        solaceTopics[topicData.SolaceTopic] -= 1;
                        if (solaceTopics[topicData.SolaceTopic] == 0)
                        {
                            UnsubscribeData(topicData);
                            solaceTopics.Remove(topicData.SolaceTopic);
                        }
                    }

                    logger.Debug("Removed data for - {0}", topicData.ToString());
                }
            }
            catch (Exception ex)
            {
                logger.Error(ex, "Exception on DisconnectData()");
            }
        }

        public int Heartbeat()
        {
            return SUCCESS;
        }

        public void ServerTerminate()
        {
            if (timer != null)
            {
                timer.Stop();
                timer.Dispose();
                timer = null;
            }

            try
            {
                // Disconnect from Solace
                Task connTask = Task.Run(async () => await solaceConnection.DisconnectAsync().ConfigureAwait(false));
                connTask.Wait();
                isSolaceConnected = false;

                // Other cleanup
                messageCache.Clear();

                logger.Info("SolaceXL RTD server has stopped gracefully");
            }
            catch (Exception ex)
            {
                logger.Error(ex, "Exception on ServerTerminate() while disconnecting from Solace");
            }
        }

        #region Timer Handler
        private void TimeEventHandler(object sender, ElapsedEventArgs e)
        {
            callback.UpdateNotify();
        }
        #endregion

        #region HelperFunctions
        private string GetData(TopicData topicData)
        {
            if (!isSolaceConnected)
                return NOT_CONNECTED_STATUS;

            // Check if we have data for this topic
            SolaceMessage message;
            if (messageCache.TryGetValue(topicData.SolaceTopic, out message))
            {
                // Get the data for the given key/fieldname from the Message 
                return message.GetData(topicData.FieldName);
            }

            return null;
        }

        private object SubscribeData(TopicData topicData)
        {
            if (isSolaceConnected)
            {
                try
                {
                    Task<bool> subTask = Task.Run(async () => await solaceConnection.SubscribeAsync(topicData.SolaceTopic).ConfigureAwait(false));
                    var subResult = subTask.Result;
                    if (subResult)
                    {
                        solaceTopics.Add(topicData.SolaceTopic, 1);
                        logger.Info("Subscription added for - {0}", topicData.ToString());
                        return GetData(topicData);
                    }
                    else
                    {
                        logger.Error("Failed to add subscription on Solace for data - {0}", topicData.ToString());
                        return ERROR_STATUS;
                    }
                }
                catch (Exception ex)
                {
                    logger.Error(ex, "Exception while subscribing for data - {0}", topicData.ToString());
                    return ERROR_STATUS;
                }
            }
            else
            {
                logger.Trace("SolaceRTD is not connected to a Solace PubSub+ Broker - {0}", topicData.ToString());
                return NOT_CONNECTED_STATUS;
            }
        }

        private bool UnsubscribeData(TopicData topicData)
        {
            var result = false;
            if (isSolaceConnected)
            {
                try
                {
                    Task<bool> subTask = Task.Run(async () => await solaceConnection.UnsubscribeAsync(topicData.SolaceTopic).ConfigureAwait(false));
                    result = subTask.Result;
                    if (result)
                    {
                        logger.Info("Removed Solace subscription to topic - {0}", topicData.SolaceTopic);
                    }
                    else
                    {
                        logger.Error("Failed to remove subscription on Solace for topic - {0}", topicData.SolaceTopic);
                    }
                }
                catch (Exception ex)
                {
                    logger.Error(ex, "Exception while unsubscribing for topic - {0}", topicData.SolaceTopic);
                }
            }
            else
            {
                logger.Trace("SolaceRTD is not connected to a Solace PubSub+ Broker - {0}", topicData.ToString());
            }

            return result;
        }
        #endregion
    }
}
