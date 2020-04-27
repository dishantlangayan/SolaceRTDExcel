using System;
using System.Text;

namespace SolaceRTDExcel.Json
{
    public class JsonSolaceMessage : SolaceMessage
    {
        private ArraySegment<byte> _body;

        public override string Body
        {
            get
            {
                if (_body.Count == 0)
                    return null;
                else
                    return Encoding.UTF8.GetString(_body.Array, _body.Offset, _body.Count);
            }
            set
            {
                if (value == null)
                    _body = new ArraySegment<byte>();
                else
                    _body = new ArraySegment<byte>(Encoding.UTF8.GetBytes(value));
            }
        }

        public override ArraySegment<byte> BodyAsBytes
        {
            get
            {
                return _body;
            }
            set
            {
                _body = value;
            }
        }

        public dynamic BodyAsJson { get; set; }

        /// <summary>
        /// Looks up the key in the Json body and return's the corresponding value.
        /// </summary>
        /// <param name="key"></param>
        /// <returns></returns>
        public override string GetData(string key)
        {
            if (BodyAsJson != null)
            {
                var jsonValue = BodyAsJson[key];
                if (jsonValue != null)
                {
                    return jsonValue.ToString();
                }
            }

            return null;
        }
    }
}
