using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace Ushell.Editor
{
    [Serializable]
    public sealed class UshellToolError
    {
        public string Code;
        public string Message;
        public object Details;
    }

    [Serializable]
    public sealed class UshellToolEnvelope
    {
        public bool Success;
        public object Data;
        public List<Dictionary<string, object>> Logs = new List<Dictionary<string, object>>();
        public List<string> Warnings = new List<string>();
        public UshellToolError Error;

        public Dictionary<string, object> ToDictionary()
        {
            return new Dictionary<string, object>
            {
                { "success", Success },
                { "data", Data },
                { "logs", Logs },
                { "warnings", Warnings },
                { "error", Error == null ? null : new Dictionary<string, object>
                    {
                        { "code", Error.Code },
                        { "message", Error.Message },
                        { "details", Error.Details }
                    }
                }
            };
        }

        public static UshellToolEnvelope FromSuccess(object data)
        {
            return new UshellToolEnvelope
            {
                Success = true,
                Data = data
            };
        }

        public static UshellToolEnvelope FromError(string code, string message, object details = null)
        {
            return new UshellToolEnvelope
            {
                Success = false,
                Error = new UshellToolError
                {
                    Code = code,
                    Message = message,
                    Details = details
                }
            };
        }
    }

    public sealed class UshellToolDefinition
    {
        public string Name;
        public string Description;
        public Dictionary<string, object> InputSchema;
        public Func<Dictionary<string, object>, UshellToolEnvelope> Handler;
        public Func<Dictionary<string, object>, Task<UshellToolEnvelope>> AsyncHandler;

        public Dictionary<string, object> ToMcpDictionary()
        {
            return new Dictionary<string, object>
            {
                { "name", Name },
                { "description", Description },
                { "inputSchema", InputSchema }
            };
        }
    }
}
