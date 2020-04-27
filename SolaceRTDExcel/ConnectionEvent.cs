using System;

namespace SolaceRTDExcel
{
    public enum ConnectionState
    {
        Closed = 4,
        Closing = 3,
        Created = 0,
        Faulted = 5,
        Opened = 2,
        Opening = 1,
        Reconnected = 7,
        Reconnecting = 6
    }

    public class ConnectionEvent
    {
        public ConnectionState State { get; set; }

        public string Info { get; set; }

        public int ResponseCode { get; set; }
    }
}
