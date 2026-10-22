using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using BepInEx;
using UnityEngine;

namespace ValheimTestKit
{
    /// <summary>
    /// Runs every <see cref="TestSuite"/> found in the loaded assemblies inside the live game and writes the
    /// result to <c>BepInEx/config/valheim-testkit/report.json</c>.
    ///
    /// Works on a dedicated server (server-side scenarios) and on a client (client-side scenarios, popups,
    /// hud). Suites declare which side they need via <see cref="TestSuite.RequiresServer"/> /
    /// <see cref="TestSuite.RequiresClient"/>; the others are reported as skipped instead of failing.
    /// </summary>
    [BepInPlugin(PluginGuid, "ValheimTestKit", "0.1.0")]
    public sealed class TestKitPlugin : BaseUnityPlugin
    {
        public const string PluginGuid = "555maggot666.ValheimTestKit";

        private const float WorldReadyTimeoutSeconds = 300f;

        private readonly List<SuiteResult> _results = new List<SuiteResult>();
        private bool _started;
        private bool _finished;
        private bool _timedOut;
        private float _awakeAt;

        private void Awake()
        {
            _awakeAt = Time.realtimeSinceStartup;
        }

        private void Update()
        {
            if (_started || _finished)
            {
                return;
            }

            if (IsWorldReady())
            {
                _started = true;
                StartCoroutine(RunAll());
                return;
            }

            if (!_timedOut && Time.realtimeSinceStartup - _awakeAt > WorldReadyTimeoutSeconds)
            {
                _timedOut = true;
                Logger.LogError("testkit: no world became ready within " + WorldReadyTimeoutSeconds +
                                "s; writing a failing report instead of hanging");
                _results.Add(SuiteResult.Aborted("world-ready", "no world became ready within " + WorldReadyTimeoutSeconds + "s"));
                _finished = true;
                WriteReport();
            }
        }

        private static bool IsWorldReady()
        {
            ZNet znet = ZNet.instance;
            if (znet == null || znet.GetWorldUID() == 0L)
            {
                return false;
            }

            return znet.IsDedicated() || Player.m_localPlayer != null;
        }

        private IEnumerator RunAll()
        {
            Logger.LogInfo("testkit: world ready (dedicated=" + ZNet.instance.IsDedicated() + "), collecting suites");
            yield return null;

            List<TestSuite> suites = DiscoverSuites();
            Logger.LogInfo("testkit: stable hash source: " + (RpcSynth.UsesHashFallback ? "fallback copy" : "game assembly"));
            Logger.LogInfo("testkit: " + suites.Count + " suite(s) found: " +
                           string.Join(", ", suites.ConvertAll(s => s.Name).ToArray()));

            if (suites.Count == 0)
            {
                Logger.LogWarning("testkit: no [TestKitSuite] class loaded; put a suite DLL next to this plugin");
            }

            bool dedicated = ZNet.instance.IsDedicated();
            for (int i = 0; i < suites.Count; i++)
            {
                TestSuite suite = suites[i];
                if ((suite.RequiresServer && !dedicated) || (suite.RequiresClient && dedicated))
                {
                    var skipped = new SuiteResult(suite.Name, skippedReason: dedicated ? "server-only suite on a client"
                                                                                      : "client-only suite on the server");
                    _results.Add(skipped);
                    Logger.LogWarning("testkit: skipping suite " + suite.Name + " (" + skipped.SkipReason + ")");
                    continue;
                }

                Logger.LogInfo("testkit: running suite " + suite.Name);
                yield return null;
                IEnumerator run = null;
                string abortReason = null;
                try
                {
                    run = suite.Run();
                }
                catch (Exception ex)
                {
                    abortReason = ex.GetType().Name + ": " + ex.Message;
                }

                while (run != null && abortReason == null)
                {
                    bool moved;
                    try
                    {
                        moved = run.MoveNext();
                    }
                    catch (Exception ex)
                    {
                        abortReason = ex.GetType().Name + ": " + ex.Message;
                        break;
                    }

                    if (!moved)
                    {
                        break;
                    }

                    yield return run.Current;
                }

                if (abortReason != null)
                {
                    // An aborted suite is a failure, never a silent green.
                    Logger.LogError("testkit: suite " + suite.Name + " aborted: " + abortReason);
                    _results.Add(new SuiteResult(suite, abortReason));
                }
                else
                {
                    _results.Add(new SuiteResult(suite));
                }
                yield return null;
            }

            _finished = true;
            WriteReport();
        }

        private List<TestSuite> DiscoverSuites()
        {
            var found = new List<TestSuite>();
            Assembly[] assemblies = AppDomain.CurrentDomain.GetAssemblies();
            for (int a = 0; a < assemblies.Length; a++)
            {
                Type[] types;
                try
                {
                    types = assemblies[a].GetTypes();
                }
                catch (ReflectionTypeLoadException ex)
                {
                    types = ex.Types;
                }
                catch (Exception)
                {
                    continue;
                }

                if (types == null)
                {
                    continue;
                }

                for (int t = 0; t < types.Length; t++)
                {
                    Type type = types[t];
                    if (type == null || type.IsAbstract || !typeof(TestSuite).IsAssignableFrom(type))
                    {
                        continue;
                    }

                    if (type.GetCustomAttribute<TestKitSuiteAttribute>() == null)
                    {
                        continue;
                    }

                    try
                    {
                        found.Add((TestSuite)Activator.CreateInstance(type));
                    }
                    catch (Exception ex)
                    {
                        Logger.LogError("testkit: could not create suite " + type.FullName + ": " + ex.Message);
                    }
                }
            }

            found.Sort((x, y) => string.CompareOrdinal(x.Name, y.Name));

            // Two suites with the same identity would make the same scenarios run twice.
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var unique = new List<TestSuite>(found.Count);
            for (int i = 0; i < found.Count; i++)
            {
                if (seen.Add(found[i].GetType().FullName + "|" + found[i].Name))
                {
                    unique.Add(found[i]);
                }
            }

            return unique;
        }

        private void WriteReport()
        {
            ZNet znet = ZNet.instance;
            string world = znet != null ? znet.GetWorldName() : null;
            bool dedicated = znet != null && znet.IsDedicated();
            int passed = 0;
            int failed = 0;
            int skipped = 0;
            var json = new StringBuilder();
            json.Append("{\n");
            json.Append("  \"generatedAt\": \"").Append(DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture)).Append("\",\n");
            json.Append("  \"world\": \"").Append(Escape(world)).Append("\",\n");
            json.Append("  \"dedicated\": ").Append(dedicated ? "true" : "false").Append(",\n");

            var suitesJson = new StringBuilder("[");
            for (int i = 0; i < _results.Count; i++)
            {
                SuiteResult result = _results[i];
                passed += result.PassedCount;
                failed += result.FailedCount;
                skipped += result.SkippedCount + (result.SkipReason != null ? 1 : 0);
                if (i > 0)
                {
                    suitesJson.Append(", ");
                }

                suitesJson.Append(result.ToJson());
            }

            suitesJson.Append(']');
            json.Append("  \"suitesFound\": ").Append(_results.Count).Append(",\n");
            json.Append("  \"passed\": ").Append(passed).Append(",\n");
            json.Append("  \"failed\": ").Append(failed).Append(",\n");
            json.Append("  \"skipped\": ").Append(skipped).Append(",\n");
            json.Append("  \"suites\": ").Append(suitesJson).Append("\n}\n");

            string dir = Path.Combine(Paths.ConfigPath, "valheim-testkit");
            Directory.CreateDirectory(dir);
            string path = Path.Combine(dir, "report.json");
            string temp = path + ".tmp";
            File.WriteAllText(temp, json.ToString());
            if (File.Exists(path))
            {
                File.Delete(path);
            }

            File.Move(temp, path);

            Logger.LogInfo("testkit: " + passed + " passed, " + failed + " failed, " + skipped + " skipped -> " + path);
            if (failed == 0)
            {
                Logger.LogInfo("TESTKIT OK");
            }
            else
            {
                Logger.LogError("TESTKIT FAILED");
            }
        }

        internal static string Escape(string value)
        {
            if (value == null)
            {
                return string.Empty;
            }

            var sb = new StringBuilder(value.Length + 8);
            foreach (char c in value)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default: sb.Append(c); break;
                }
            }

            return sb.ToString();
        }
    }

    internal sealed class SuiteResult
    {
        private string _name;
        private readonly List<string> _passed;
        private readonly List<string> _failed;
        private readonly List<string> _skipped;
        private readonly List<string> _details;

        public SuiteResult(TestSuite suite)
        {
            _name = suite.Name;
            _passed = new List<string>(suite.Passed);
            _failed = new List<string>(suite.Failed);
            _skipped = new List<string>(suite.Skipped);
            _details = new List<string>(suite.Details);
        }

        /// <summary>A suite that aborted part-way: keeps its partial results and marks the abort as a failure.</summary>
        public SuiteResult(TestSuite suite, string abortReason)
        {
            _name = suite.Name;
            _passed = new List<string>(suite.Passed);
            _failed = new List<string>(suite.Failed);
            _skipped = new List<string>(suite.Skipped);
            _details = new List<string>(suite.Details);
            _failed.Add("suite-aborted: " + abortReason);
        }

        public SuiteResult(string name, string skippedReason)
        {
            _name = name;
            _passed = new List<string>();
            _failed = new List<string>();
            _skipped = new List<string>();
            _details = new List<string>();
            SkipReason = skippedReason;
        }

        private SuiteResult()
        {
            _passed = new List<string>();
            _failed = new List<string>();
            _skipped = new List<string>();
            _details = new List<string>();
        }

        /// <summary>A suite that never produced a verdict (crash, timeout) — reported as one failure.</summary>
        public static SuiteResult Aborted(string name, string reason)
        {
            var result = new SuiteResult();
            result._name = name;
            result._failed.Add("suite-aborted: " + reason);
            return result;
        }

        public string Name => _name;

        public string SkipReason { get; }

        public int PassedCount => _passed.Count;

        public int FailedCount => _failed.Count;

        public int SkippedCount => _skipped.Count;

        public string ToJson()
        {
            var sb = new StringBuilder();
            sb.Append("{\"name\": \"").Append(TestKitPlugin.Escape(_name)).Append("\", ");
            sb.Append("\"passed\": ").Append(PassedCount).Append(", ");
            sb.Append("\"failed\": ").Append(FailedCount).Append(", ");
            sb.Append("\"skipped\": ").Append(SkippedCount).Append(", ");
            if (SkipReason != null)
            {
                sb.Append("\"skipReason\": \"").Append(TestKitPlugin.Escape(SkipReason)).Append("\", ");
            }

            sb.Append("\"failedNames\": ").Append(Array(_failed)).Append(", ");
            sb.Append("\"passedNames\": ").Append(Array(_passed)).Append(", ");
            sb.Append("\"details\": ").Append(Array(_details)).Append('}');
            return sb.ToString();
        }

        private static string Array(List<string> values)
        {
            var sb = new StringBuilder("[");
            for (int i = 0; i < values.Count; i++)
            {
                if (i > 0)
                {
                    sb.Append(", ");
                }

                sb.Append('"').Append(TestKitPlugin.Escape(values[i])).Append('"');
            }

            return sb.Append(']').ToString();
        }
    }
}
