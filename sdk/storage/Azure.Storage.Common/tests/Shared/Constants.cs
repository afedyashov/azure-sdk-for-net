﻿// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License. See License.txt in the project root for
// license information.

using System;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using Azure.Storage.Common;
using Azure.Storage.Test.Shared;

namespace Azure.Storage.Test
{
    internal class Constants
    {
        public const int KB = 1024;
        public const int MB = KB * 1024;
        public const int GB = MB * 1024;
        public const long TB = GB * 1024L;

        public string CacheControl { get; private set; }
        public string ContentDisposition { get; private set; }
        public string ContentEncoding { get; private set; }
        public string ContentLanguage { get; private set; }
        public string ContentType { get; private set; }
        public byte[] ContentMD5 { get; private set; }
        public SasConstants Sas { get; private set; }

        internal class SasConstants
        {
            public string KeyOid { get; } = "KeyOid";
            public string KeyTid { get; } = "KeyTid";
            public string KeyService { get; } = "KeyService";
            public string KeyVersion { get; } = "KeyVersion";
            public string KeyValue { get; } = Convert.ToBase64String(Encoding.UTF8.GetBytes("value"));
            public SasProtocol Protocol { get; } = SasProtocol.Https;

            public string Version { get; protected internal set; }
            public string Account { get; protected internal set; }
            public string Identifier { get; protected internal set; }
            public string CacheControl { get; protected internal set; }
            public string ContentDisposition { get; protected internal set; }
            public string ContentEncoding { get; protected internal set; }
            public string ContentLanguage { get; protected internal set; }
            public string ContentType { get; protected internal set; }
            public string AccountKey { get; protected internal set; }
            public DateTimeOffset StartTime { get; protected internal set; }
            public DateTimeOffset ExpiryTime { get; protected internal set; }
            public IPAddress StartAddress { get; protected internal set; }
            public IPAddress EndAddress { get; protected internal set; }
            public IPRange IPRange { get; protected internal set; }
            public DateTimeOffset KeyStart { get; protected internal set; }
            public DateTimeOffset KeyExpiry { get; protected internal set; }
            public SharedKeyCredentials SharedKeyCredential { get; protected internal set; }
        }

        public Constants(StorageTestBase test)
        {
            this.CacheControl = test.GetNewString();
            this.ContentDisposition = test.GetNewString();
            this.ContentEncoding = test.GetNewString();
            this.ContentLanguage = test.GetNewString();
            this.ContentType = test.GetNewString();
            this.ContentMD5 = MD5.Create().ComputeHash(test.GetRandomBuffer(16));

            this.Sas = new SasConstants
            {
                Version = test.GetNewString(),
                Account = test.GetNewString(),
                Identifier = test.GetNewString(),
                CacheControl = test.GetNewString(),
                ContentDisposition = test.GetNewString(),
                ContentEncoding = test.GetNewString(),
                ContentLanguage = test.GetNewString(),
                ContentType = test.GetNewString(),
                AccountKey = Convert.ToBase64String(Encoding.UTF8.GetBytes(test.GetNewString())),
                StartTime = test.GetUtcNow().AddHours(-1),
                ExpiryTime = test.GetUtcNow().AddHours(+1),
                StartAddress = test.GetIPAddress(),
                EndAddress = test.GetIPAddress(),
                KeyStart = test.GetUtcNow().AddHours(-1),
                KeyExpiry = test.GetUtcNow().AddHours(+1)
            };
            this.Sas.IPRange = new IPRange { Start = this.Sas.StartAddress, End = this.Sas.EndAddress };
            this.Sas.SharedKeyCredential = new SharedKeyCredentials(this.Sas.Account, this.Sas.AccountKey);
        }
    }
}
