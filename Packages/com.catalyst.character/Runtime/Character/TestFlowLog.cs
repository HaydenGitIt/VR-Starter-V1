using System;
using System.Collections;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using UnityEngine;
using Debug = UnityEngine.Debug;
using Object = UnityEngine.Object;

namespace catalyst.xr67
{
    /// <summary>
    /// One log line per conversation / animation event, written so a person
    /// can read it in the Console and a tool can rebuild the test flow from
    /// the Unity log (Editor.log / Player.log). Each entry is two lines:
    ///
    ///   [TestFlow] You said: "hello"
    ///   [TestFlow:data] {"tf":1,"session":"…","seq":3,"ev":"user","t":12.345,…}
    ///
    /// The data line is single-line JSON. reportBuilder/build_report.py reads
    /// it. Event names (ev): session, user, user_dropped, silence,
    /// llm_reply, llm_error, mel_line, mel_line_failed, speech_start,
    /// speech_end, phase, oneshot_start, oneshot_end, gesture_skipped,
    /// gesture_queued.
    ///
    /// Entries are logged without a stack trace so the log stays readable.
    /// Safe to call from any thread.
    /// </summary>
    public static class TestFlowLog
    {
        /// <summary>Master switch. Off = nothing is logged.</summary>
        public static bool Enabled = true;

        public const string Tag = "[TestFlow]";
        public const string DataTag = "[TestFlow:data]";

        private static readonly object Gate = new object();
        private static readonly Stopwatch Clock = new Stopwatch();
        private static string _session;
        private static int _seq;

        // Also runs with domain reload off, so every Play Mode run starts a new session.
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetSession()
        {
            lock (Gate)
            {
                _session = null;
                _seq = 0;
                Clock.Reset();
            }
        }

        /// <summary>
        /// Logs one event. <paramref name="fields"/> are key/value pairs:
        /// "text", line, "mood", 72, … Values may be strings, numbers, bools,
        /// null or lists of strings.
        /// </summary>
        public static void Emit(string ev, string human, Object context, params object[] fields)
        {
            if (!Enabled) return;
            string sessionLine = null;
            string line;
            lock (Gate)
            {
                if (_session == null)
                {
                    _session = DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + "-" + Guid.NewGuid().ToString("N").Substring(0, 6);
                    Clock.Restart();
                    sessionLine = Format("session", "Test session started", SessionFields());
                }
                line = Format(ev, human, fields);
            }
            if (sessionLine != null) Write(sessionLine, null);
            Write(line, context);
        }

        private static object[] SessionFields()
        {
            string scene = "";
            try { scene = UnityEngine.SceneManagement.SceneManager.GetActiveScene().name; }
            catch (Exception) { /* off the main thread — leave it empty */ }
            return new object[]
            {
                "scene", scene,
                "product", SafeGet(() => Application.productName),
                "unity", SafeGet(() => Application.unityVersion),
#if UNITY_EDITOR
                "editor", true,
#else
                "editor", false,
#endif
            };
        }

        private static string SafeGet(Func<string> get)
        {
            try { return get(); }
            catch (Exception) { return ""; }
        }

        private static string Format(string ev, string human, object[] fields)
        {
            var json = new StringBuilder(256);
            json.Append("{\"tf\":1,\"session\":");
            AppendString(json, _session);
            json.Append(",\"seq\":").Append(++_seq);
            json.Append(",\"ev\":");
            AppendString(json, ev);
            json.Append(",\"t\":").Append((Clock.Elapsed.TotalSeconds).ToString("0.000", CultureInfo.InvariantCulture));
            json.Append(",\"wall\":");
            AppendString(json, DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss.fff", CultureInfo.InvariantCulture));
            if (fields != null)
            {
                for (int i = 0; i + 1 < fields.Length; i += 2)
                {
                    json.Append(',');
                    AppendString(json, fields[i] as string ?? fields[i]?.ToString() ?? "key");
                    json.Append(':');
                    AppendValue(json, fields[i + 1]);
                }
            }
            json.Append('}');

            // Keep the readable line on one line too; the data line carries the full text.
            string readable = (human ?? ev).Replace("\r", " ").Replace("\n", " ");
            return $"{Tag} {readable}\n{DataTag} {json}";
        }

        private static void Write(string text, Object context)
        {
            Debug.LogFormat(LogType.Log, LogOption.NoStacktrace, context, "{0}", text);
        }

        private static void AppendValue(StringBuilder sb, object value)
        {
            switch (value)
            {
                case null: sb.Append("null"); break;
                case string s: AppendString(sb, s); break;
                case bool b: sb.Append(b ? "true" : "false"); break;
                case float f: sb.Append(float.IsFinite(f) ? f.ToString("0.###", CultureInfo.InvariantCulture) : "null"); break;
                case double d: sb.Append(double.IsFinite(d) ? d.ToString("0.###", CultureInfo.InvariantCulture) : "null"); break;
                case int or long or short or byte or uint or ulong:
                    sb.Append(Convert.ToString(value, CultureInfo.InvariantCulture)); break;
                case Enum e: AppendString(sb, e.ToString().ToLowerInvariant()); break;
                case IEnumerable list:
                    sb.Append('[');
                    bool first = true;
                    foreach (object item in list)
                    {
                        if (!first) sb.Append(',');
                        first = false;
                        AppendValue(sb, item);
                    }
                    sb.Append(']');
                    break;
                default: AppendString(sb, value.ToString()); break;
            }
        }

        private static void AppendString(StringBuilder sb, string s)
        {
            sb.Append('"');
            if (s != null)
            {
                foreach (char c in s)
                {
                    switch (c)
                    {
                        case '"': sb.Append("\\\""); break;
                        case '\\': sb.Append("\\\\"); break;
                        case '\n': sb.Append("\\n"); break;
                        case '\r': sb.Append("\\r"); break;
                        case '\t': sb.Append("\\t"); break;
                        default:
                            if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4"));
                            else sb.Append(c);
                            break;
                    }
                }
            }
            sb.Append('"');
        }
    }
}
