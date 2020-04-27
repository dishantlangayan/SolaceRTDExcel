using System;

namespace SolaceRTDExcel
{
    /// <summary>
    /// Custom Messaging exception class for the SolaceDotNetWrapper API.
    /// </summary>
    public class MessagingException : Exception
    {
        /// <summary>
        ///     Empty Constructor.
        /// </summary>
        public MessagingException()
        {
        }

        /// <summary>
        ///     Constructor with message.
        /// </summary>
        /// <param name="message">Description of the exception. </param>
        public MessagingException(string message)
            : base(message)
        {
        }

        /// <summary>
        ///     Constructor with message and inner exception.
        /// </summary>
        /// <param name="message">Description of the exception. </param>
        /// <param name="inner">Inner Exception.</param>
        public MessagingException(string message, Exception inner)
            : base(message, inner)
        {
        }
    }
}