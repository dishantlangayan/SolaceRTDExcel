using System;
using System.Runtime.InteropServices;

namespace SolaceRTDExcel.Json
{
    [ComVisible(true)]
    [ClassInterface(ClassInterfaceType.None)]
    public class JsonMessageConvertorFactory : MessageConvertorFactory<JsonMessageConvertor>
    {
        public override JsonMessageConvertor CreateConvertor()
        {
            return new JsonMessageConvertor();
        }
    }
}
