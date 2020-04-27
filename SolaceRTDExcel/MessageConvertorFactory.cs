using System;
using System.Runtime.InteropServices;

namespace SolaceRTDExcel
{
    [ComVisible(false)]
    [ClassInterface(ClassInterfaceType.None)]
    public abstract class MessageConvertorFactory<T>
    {
        public abstract T CreateConvertor();
    }
}
