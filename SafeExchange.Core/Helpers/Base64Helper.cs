/// SafeExchange

namespace SpaceOyster.SafeExchange.Core
{
    using System;
    using System.Text;

    public static class Base64Helper
    {
        public static string StringToBase64(string toEncode)
        {
            var toEncodeAsBytes = Encoding.UTF8.GetBytes(toEncode);
            return Convert.ToBase64String(toEncodeAsBytes);
        }

        public static string Base64ToString(string toDecode)
        {
            var encodedDataAsBytes = Convert.FromBase64String(toDecode);
            return Encoding.UTF8.GetString(encodedDataAsBytes);
        }
    }
}