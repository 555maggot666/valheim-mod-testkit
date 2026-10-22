using System;
using System.Collections;
using System.Collections.Generic;
using BepInEx.Logging;

namespace ValheimTestKit
{
    /// <summary>Marks a class as a test suite that <see cref="TestKitPlugin"/> runs inside the live game.</summary>
    [AttributeUsage(AttributeTargets.Class)]
    public sealed class TestKitSuiteAttribute : Attribute
    {
        public TestKitSuiteAttribute(string name = null)
        {
            Name = name;
        }

        public string Name { get; }
    }

    /// <summary>
    /// Base class for a suite. It runs in the Unity main thread inside the game, so a scenario may yield to let
    /// the game tick (world load, RPC delivery, hud construction) or wait in real time.
    /// </summary>
    public abstract class TestSuite
    {
        private readonly List<string> _passed = new List<string>();
        private readonly List<string> _failed = new List<string>();
        private readonly List<string> _skipped = new List<string>();
        private readonly List<string> _details = new List<string>();

        protected TestSuite()
        {
            Log = BepInEx.Logging.Logger.CreateLogSource(GetType().Name);
        }

        public ManualLogSource Log { get; }

        /// <summary>Name shown in the report; defaults to the attribute name or the class name.</summary>
        public virtual string Name => GetType().Name;

        /// <summary>Only run this suite when the process is a dedicated server.</summary>
        public virtual bool RequiresServer => false;

        /// <summary>Only run this suite when the process is a client with a local player.</summary>
        public virtual bool RequiresClient => false;

        public IReadOnlyList<string> Passed => _passed;

        public IReadOnlyList<string> Failed => _failed;

        public IReadOnlyList<string> Skipped => _skipped;

        public IReadOnlyList<string> Details => _details;

        public abstract IEnumerator Run();

        // ---------------------------------------------------------------- scenario helpers

        /// <summary>Runs one scenario, turning a thrown exception into a failure instead of aborting the suite.</summary>
        protected IEnumerator Scenario(string name, Func<IEnumerator> body)
        {
            IEnumerator inner = null;
            try
            {
                inner = body();
            }
            catch (Exception ex)
            {
                Fail(name, ex);
            }

            if (inner == null)
            {
                yield break;
            }

            object current = null;
            while (true)
            {
                bool moved;
                try
                {
                    moved = inner.MoveNext();
                    current = inner.Current;
                }
                catch (Exception ex)
                {
                    Fail(name, ex);
                    yield break;
                }

                if (!moved)
                {
                    // A scenario that reported itself as skipped must not also count as passed.
                    if (!_failed.Contains(name) && !_skipped.Contains(name))
                    {
                        Pass(name);
                    }

                    yield break;
                }

                yield return current;
            }
        }

        /// <summary>Runs a synchronous scenario.</summary>
        protected IEnumerator Scenario(string name, Action body)
        {
            return Scenario(name, () =>
            {
                body();
                return Empty();
            });
        }

        protected static void Assert(bool condition, string message)
        {
            if (!condition)
            {
                throw new AssertionException(message);
            }
        }

        protected static void AssertEqual<T>(T expected, T actual, string what)
        {
            if (!EqualityComparer<T>.Default.Equals(expected, actual))
            {
                throw new AssertionException(what + ": expected " + expected + ", got " + actual);
            }
        }

        protected static IEnumerator Empty()
        {
            yield break;
        }

        /// <summary>Waits <paramref name="seconds"/> of real (unscaled) time while the game keeps running.</summary>
        protected static IEnumerator Wait(float seconds)
        {
            float until = UnityEngine.Time.realtimeSinceStartup + seconds;
            while (UnityEngine.Time.realtimeSinceStartup < until)
            {
                yield return null;
            }
        }

        /// <summary>Waits until <paramref name="condition"/> is true or the timeout expires. Returns whether it became true.</summary>
        protected static IEnumerator WaitUntil(Func<bool> condition, float timeoutSeconds, Action<bool> result = null)
        {
            float until = UnityEngine.Time.realtimeSinceStartup + timeoutSeconds;
            while (UnityEngine.Time.realtimeSinceStartup < until)
            {
                if (condition())
                {
                    result?.Invoke(true);
                    yield break;
                }

                yield return null;
            }

            result?.Invoke(condition());
        }

        protected void Pass(string name)
        {
            _passed.Add(name);
            Log.LogInfo("PASS " + name);
        }

        protected void Fail(string name, Exception ex)
        {
            _failed.Add(name);
            string message = ex.GetType().Name + ": " + ex.Message;
            _details.Add(name + ": " + message);
            Log.LogError("FAIL " + name + " - " + message);
        }

        protected void Fail(string name, string message)
        {
            _failed.Add(name);
            _details.Add(name + ": " + message);
            Log.LogError("FAIL " + name + " - " + message);
        }

        protected void Skip(string name, string reason)
        {
            _skipped.Add(name);
            _details.Add(name + " (skip): " + reason);
            Log.LogWarning("SKIP " + name + " - " + reason);
        }

        protected void Detail(string line)
        {
            _details.Add(line);
            Log.LogInfo(line);
        }
    }

    public sealed class AssertionException : Exception
    {
        public AssertionException(string message) : base(message)
        {
        }
    }
}
