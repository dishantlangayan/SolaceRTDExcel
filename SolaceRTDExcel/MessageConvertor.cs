using SolaceSystems.Solclient.Messaging;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace SolaceRTDExcel
{
    public abstract class MessageConvertor
    {
        public abstract SolaceMessage ConvertMessage(IMessage message);
    }
}
