using SolaceSystems.Solclient.Messaging;
using System;
using Newtonsoft.Json;
using System.Runtime.InteropServices;

namespace SolaceRTDExcel.Json
{
    [ComVisible(true)]
    [ClassInterface(ClassInterfaceType.None)]
    public class JsonMessageConvertor : MessageConvertor
    {
        private static NLog.Logger logger = NLog.LogManager.GetCurrentClassLogger();

        public override SolaceMessage ConvertMessage(IMessage message)
        {
            var jsonMsg = new JsonSolaceMessage();

            // Destination
            jsonMsg.Destination = message.Destination.Name;

            // Copy binary contents
            sbyte[] bodyBytes = message.GetBinaryAttachment();
            if (bodyBytes != null)
                jsonMsg.BodyAsBytes = new ArraySegment<byte>((byte[])((Array)bodyBytes));

            // Try to convert binary contents to Json
            try
            {
                string bodyAsString = jsonMsg.Body;
                if (!string.IsNullOrEmpty(bodyAsString))
                    jsonMsg.BodyAsJson = JsonConvert.DeserializeObject<dynamic>(bodyAsString);
            }
            catch (Exception e)
            {
                // Probably not a json message or well formated json
                logger.Error(e, "Unable to convert received message to a Json Message.");
            }

            return jsonMsg;
        }
    }
}
