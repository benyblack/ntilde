using System;
using System.Collections.Generic;

namespace Ntilde.ExternalSuites.Vttest
{
    public abstract record VttestStep;
    public record SendBytes(byte[] Bytes) : VttestStep;
    public record WaitForQuiet(int QuietMs, int MaxWaitMs) : VttestStep;

    public static class VttestPlan
    {
        private static readonly byte[] Enter = new byte[] { (byte)'\r' };
        private static readonly byte[] Q = new byte[] { (byte)'q' };

        private static SendBytes Sel(char n) => new SendBytes(new byte[] { (byte)n, (byte)'\r' });

        public static IEnumerable<VttestStep> GetCursorScenario() => new List<VttestStep>
        {
            new WaitForQuiet(300, 5000),
            Sel('1'),
            new WaitForQuiet(300, 5000),

            // many vttest pages require "press any key"
            new SendBytes(Enter),
            new WaitForQuiet(300, 5000),

            new SendBytes(Q),
            new WaitForQuiet(300, 5000),

            new SendBytes(Q),
            new WaitForQuiet(300, 5000),
        };

        public static IEnumerable<VttestStep> GetSgrScenario() => new List<VttestStep>
        {
            new WaitForQuiet(300, 5000),
            Sel('3'),
            new WaitForQuiet(300, 8000),

            new SendBytes(Enter),
            new WaitForQuiet(300, 5000),

            new SendBytes(Q),
            new WaitForQuiet(300, 5000),

            new SendBytes(Q),
            new WaitForQuiet(300, 5000),
        };

        public static IEnumerable<VttestStep> GetScrollScenario() => new List<VttestStep>
        {
            new WaitForQuiet(300, 5000),
            Sel('2'),
            new WaitForQuiet(300, 8000),

            new SendBytes(Enter),
            new WaitForQuiet(300, 5000),

            new SendBytes(Q),
            new WaitForQuiet(300, 5000),

            new SendBytes(Q),
            new WaitForQuiet(300, 5000),
        };
    }
}
