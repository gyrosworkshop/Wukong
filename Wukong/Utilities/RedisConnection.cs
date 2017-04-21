﻿using System;
using System.Linq;
using System.Net;

namespace Wukong.Utilities
{
    public class RedisConnection
    {
        public static string GetConnectionString(string originalConnectionString)
        {
            if (String.IsNullOrEmpty(originalConnectionString))
            {
                return null;
            }
            var host = originalConnectionString.Split(':')[0];
            var ipAddress = Dns.GetHostEntryAsync(host).Result.AddressList.Last();
            return ipAddress + (originalConnectionString.Split(':').Length == 2 ? ":" + originalConnectionString.Split(':')[1] : "");
        }
    }
}
