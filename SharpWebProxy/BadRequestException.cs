using System;

namespace SharpWebProxy
{
    public class BadRequestException: Exception
    {
        public BadRequestException(string message) : base(message)
        {
        }
        
    }
}