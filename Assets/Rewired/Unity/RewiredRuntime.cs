using System;
using Rewired.Core.Simulation;
using UnityEngine;

namespace Rewired.Unity
{
    public sealed class RewiredRuntime : MonoBehaviour
    {
        [SerializeField, Min(1)] int targetStepsPerSecond = 10000;
        [SerializeField, Min(1)] int maxStepsPerFrame = 1000;

        double accumulator;

        public SimulationEngine Engine { get; private set; }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        static void CreateRuntime()
        {
            var host = new GameObject("[Rewired Runtime]");
            DontDestroyOnLoad(host);
            host.AddComponent<RewiredRuntime>();
        }

        void Awake()
        {
            Engine = new SimulationEngine();
            Application.runInBackground = true;
        }

        void Update()
        {
            if (Engine == null || targetStepsPerSecond < 1) return;

            double stepDuration = 1.0 / targetStepsPerSecond;
            accumulator += Time.unscaledDeltaTime;

            int steps = 0;
            while (accumulator >= stepDuration && steps < maxStepsPerFrame)
            {
                Engine.RunStep();
                accumulator -= stepDuration;
                steps++;
            }

            if (steps == maxStepsPerFrame && accumulator > stepDuration * maxStepsPerFrame)
            {
                accumulator = stepDuration * maxStepsPerFrame;
            }
        }

        public void Register(ICombinationalComponent component)
        {
            if (Engine == null) throw new InvalidOperationException("Runtime is not initialized.");
            Engine.Add(component);
        }

        public void Register(ITickComponent component)
        {
            if (Engine == null) throw new InvalidOperationException("Runtime is not initialized.");
            Engine.Add(component);
        }
    }
}
