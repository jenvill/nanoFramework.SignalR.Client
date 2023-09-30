// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
#nullable enable

using nanoFramework.Json;
using System;
using System.Collections;

namespace nanoFramework.SignalR.Client
{
    internal class InvocationSendMessage
    {
        public MessageType Type { get; set; }
        public string Target { get; set; } = null!;
        public string InvocationId { get; set; } = null!;
        public object[] Arguments { get; set; } = null!;
        public object[] StreamIds { get; set; } = null!;

        private string ValueToString(object? value) 
            => value switch
            {
                null => "null",
                DateTime d => $"\"{d.ToString("O")}\"",
                Guid or char => $"\"{value}\"",
                bool b => b.ToString().ToLower(),
                byte[] ba => $"\"{Convert.ToBase64String(ba)}\"",
                var v when v.GetType().IsClass => JsonConvert.SerializeObject(v),
                string s => $"\"{EscapeString(s)}\"",
                _ => value.ToString(),

            };
        private string EscapeString(string value)
        {
            var charArray = value.ToCharArray();
            for (int i = 0; i < charArray.Length; i++)
            {
                char c = charArray[i];
                if (c == '"') charArray[i] = '\"';
            }
            return new string(charArray);
        }

        public override string ToString()
        {
            string args = string.Empty;
            string streamIds = string.Empty;
            try
            {
                if (Arguments != null)
                {
                    for (int i = 0; i < Arguments.Length; i++)
                    {
                        if (i == 0) args += ValueToString(Arguments[i]);
                        else args += $", {ValueToString(Arguments[i])}";
                    }
                }
            }
            catch (Exception ex)
            {
                args = $"Error: {ex.Message}";
            }

            try
            {
                if (StreamIds != null)
                {
                    for (int i = 0; i < StreamIds.Length; i++)
                    {
                        if (i == 0) streamIds += ValueToString(StreamIds[i]);
                        else streamIds += $", {ValueToString(StreamIds[i])}";
                    }
                }
            }
            catch (Exception ex)
            {
                streamIds = $"Error: {ex.Message}";
            }

            return $"{{ \"type\": {Type},\"invocationId\": \"{InvocationId}\", \"target\": \"{Target}\", \"arguments\": [ {args} ], \"streamIds\": [ {streamIds} ] }}";
        }
    }
}
