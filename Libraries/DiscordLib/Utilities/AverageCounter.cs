﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace DiscordLib.Utilities
{
    internal class AverageCounter
    {
        private double[] samples;
        private int sampleIndex;
        private int sampleCount;

        public AverageCounter() : this(100) { }
        public AverageCounter(int sampleCount)
        {
            samples = new double[sampleCount];
            sampleIndex = 0;
            this.sampleCount = 0;
        }

        public void SubmitSample(double sample)
        {
            lock (samples)
            {
                samples[sampleIndex] = sample;
                sampleIndex = (sampleIndex + 1) % samples.Length;
                sampleCount++;
            }
        }

        public double GetAverage()
        {
            int x;
            double d, avg;
            GetAverage(out x, out d, out d, out avg);

            return avg;
        }

        public void GetAverage(out int count, out double min, out double max, out double avg)
        {
            count = Math.Min(sampleCount, samples.Length);

            var x = 0.0;
            min = double.PositiveInfinity;
            max = double.NegativeInfinity;
            for (int i = 0; i < count; i++)
            {
                x += samples[i];
                min = Math.Min(min, samples[i]);
                max = Math.Max(max, samples[i]);
            }

            avg = x / count;
        }
    }
}
