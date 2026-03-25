using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace Ushell.Editor
{
    public static class MiniJson
    {
        public static object Deserialize(string json)
        {
            if (json == null)
            {
                return null;
            }

            Parser parser = new Parser(json);
            return parser.ParseValue();
        }

        public static string Serialize(object value)
        {
            StringBuilder builder = new StringBuilder(256);
            Serializer.SerializeValue(value, builder);
            return builder.ToString();
        }

        private sealed class Parser
        {
            private readonly StringReader _reader;

            public Parser(string json)
            {
                _reader = new StringReader(json);
            }

            public object ParseValue()
            {
                EatWhitespace();
                int next = Peek();
                switch (next)
                {
                    case -1: return null;
                    case '{': return ParseObject();
                    case '[': return ParseArray();
                    case '"': return ParseString();
                    case 't': return ParseLiteral("true", true);
                    case 'f': return ParseLiteral("false", false);
                    case 'n': return ParseLiteral("null", null);
                    default: return ParseNumber();
                }
            }

            private Dictionary<string, object> ParseObject()
            {
                Read();
                Dictionary<string, object> dictionary = new Dictionary<string, object>();
                while (true)
                {
                    EatWhitespace();
                    int next = Peek();
                    if (next == '}')
                    {
                        Read();
                        return dictionary;
                    }

                    string key = ParseString();
                    EatWhitespace();
                    Read();
                    object value = ParseValue();
                    dictionary[key] = value;
                    EatWhitespace();
                    next = Read();
                    if (next == '}')
                    {
                        return dictionary;
                    }
                }
            }

            private List<object> ParseArray()
            {
                Read();
                List<object> list = new List<object>();
                while (true)
                {
                    EatWhitespace();
                    int next = Peek();
                    if (next == ']')
                    {
                        Read();
                        return list;
                    }

                    list.Add(ParseValue());
                    EatWhitespace();
                    next = Read();
                    if (next == ']')
                    {
                        return list;
                    }
                }
            }

            private object ParseLiteral(string token, object value)
            {
                for (int index = 0; index < token.Length; index++)
                {
                    Read();
                }

                return value;
            }

            private string ParseString()
            {
                Read();
                StringBuilder builder = new StringBuilder();
                while (true)
                {
                    int character = Read();
                    if (character == -1 || character == '"')
                    {
                        break;
                    }

                    if (character == '\\')
                    {
                        int escaped = Read();
                        switch (escaped)
                        {
                            case '"': builder.Append('"'); break;
                            case '\\': builder.Append('\\'); break;
                            case '/': builder.Append('/'); break;
                            case 'b': builder.Append('\b'); break;
                            case 'f': builder.Append('\f'); break;
                            case 'n': builder.Append('\n'); break;
                            case 'r': builder.Append('\r'); break;
                            case 't': builder.Append('\t'); break;
                            case 'u':
                                char[] buffer = new char[4];
                                _reader.Read(buffer, 0, 4);
                                builder.Append((char)Convert.ToInt32(new string(buffer), 16));
                                break;
                        }
                    }
                    else
                    {
                        builder.Append((char)character);
                    }
                }

                return builder.ToString();
            }

            private object ParseNumber()
            {
                StringBuilder builder = new StringBuilder();
                while (true)
                {
                    int next = Peek();
                    if (next == -1 || " \t\r\n,]}".IndexOf((char)next) >= 0)
                    {
                        break;
                    }

                    builder.Append((char)Read());
                }

                string number = builder.ToString();
                if (number.IndexOf('.') >= 0 || number.IndexOf('e') >= 0 || number.IndexOf('E') >= 0)
                {
                    return double.Parse(number, CultureInfo.InvariantCulture);
                }

                if (long.TryParse(number, NumberStyles.Integer, CultureInfo.InvariantCulture, out long integer))
                {
                    return integer;
                }

                return 0L;
            }

            private void EatWhitespace()
            {
                while (true)
                {
                    int peek = Peek();
                    if (peek == -1 || !char.IsWhiteSpace((char)peek))
                    {
                        break;
                    }

                    Read();
                }
            }

            private int Peek()
            {
                return _reader.Peek();
            }

            private int Read()
            {
                return _reader.Read();
            }
        }

        private static class Serializer
        {
            public static void SerializeValue(object value, StringBuilder builder)
            {
                switch (value)
                {
                    case null:
                        builder.Append("null");
                        return;
                    case string stringValue:
                        SerializeString(stringValue, builder);
                        return;
                    case bool boolValue:
                        builder.Append(boolValue ? "true" : "false");
                        return;
                    case IDictionary dictionary:
                        SerializeObject(dictionary, builder);
                        return;
                    case IList list:
                        SerializeArray(list, builder);
                        return;
                    case Enum _:
                        SerializeString(value.ToString(), builder);
                        return;
                }

                if (value is char character)
                {
                    SerializeString(character.ToString(), builder);
                    return;
                }

                if (value is byte || value is sbyte || value is short || value is ushort || value is int || value is uint || value is long || value is ulong || value is float || value is double || value is decimal)
                {
                    builder.Append(Convert.ToString(value, CultureInfo.InvariantCulture));
                    return;
                }

                SerializeString(value.ToString(), builder);
            }

            private static void SerializeObject(IDictionary dictionary, StringBuilder builder)
            {
                bool first = true;
                builder.Append('{');
                foreach (DictionaryEntry entry in dictionary)
                {
                    if (!first)
                    {
                        builder.Append(',');
                    }

                    SerializeString(entry.Key.ToString(), builder);
                    builder.Append(':');
                    SerializeValue(entry.Value, builder);
                    first = false;
                }

                builder.Append('}');
            }

            private static void SerializeArray(IList list, StringBuilder builder)
            {
                bool first = true;
                builder.Append('[');
                foreach (object entry in list)
                {
                    if (!first)
                    {
                        builder.Append(',');
                    }

                    SerializeValue(entry, builder);
                    first = false;
                }

                builder.Append(']');
            }

            private static void SerializeString(string value, StringBuilder builder)
            {
                builder.Append('"');
                foreach (char character in value)
                {
                    switch (character)
                    {
                        case '"': builder.Append("\\\""); break;
                        case '\\': builder.Append("\\\\"); break;
                        case '\b': builder.Append("\\b"); break;
                        case '\f': builder.Append("\\f"); break;
                        case '\n': builder.Append("\\n"); break;
                        case '\r': builder.Append("\\r"); break;
                        case '\t': builder.Append("\\t"); break;
                        default:
                            if (character < 32)
                            {
                                builder.Append("\\u");
                                builder.Append(((int)character).ToString("x4"));
                            }
                            else
                            {
                                builder.Append(character);
                            }
                            break;
                    }
                }

                builder.Append('"');
            }
        }
    }
}
