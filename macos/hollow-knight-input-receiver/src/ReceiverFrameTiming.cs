using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;

namespace HollowKnightVisionInputReceiver
{
    // Diagnostic only: retain ten seconds, write a snapshot off Unity's thread
    // after a slow frame. Never write files or format JSON in the frame loop.
    internal sealed class ReceiverFrameTiming
    {
        private struct Sample
        {
            public int Frame, Collections;
            public double Start, Duration, Update, LateStart, Late, Pose, Render, Gui, Scale;
        }
        private readonly string path;
        private readonly Sample[] recent = new Sample[600];
        private Sample current;
        private bool active;
        private int cursor, count, writing;
        private double lastWrite;
        internal static double Now { get { return Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency; } }
        internal ReceiverFrameTiming(string path) { this.path = path; }
        internal void Begin(int frame, double scale, bool enabled)
        {
            var now = Now;
            if (active) {
                current.Duration = (now - current.Start) * 1000;
                current.Collections = GC.CollectionCount(2) - current.Collections;
                recent[cursor] = current;
                cursor = (cursor + 1) % recent.Length;
                count = Math.Min(count + 1, recent.Length);
                if (current.Duration > 40 && now - lastWrite > 2
                    && Interlocked.CompareExchange(ref writing, 1, 0) == 0) {
                    lastWrite = now;
                    var copy = new Sample[count];
                    for (var i = 0; i < count; i++) copy[i] = recent[(cursor - count + i + recent.Length) % recent.Length];
                    ThreadPool.QueueUserWorkItem(_ => Write(copy));
                }
            }
            active = enabled;
            current = new Sample { Frame = frame, Start = now, Scale = scale, Collections = GC.CollectionCount(2) };
        }
        internal void EndUpdate() { current.Update = (Now - current.Start) * 1000; }
        internal void BeginLate() { current.LateStart = (Now - current.Start) * 1000; }
        internal void EndLate() { current.Late = (Now - current.Start) * 1000 - current.LateStart; }
        internal void Pose(double before) { current.Pose = (Now - before) * 1000; }
        internal void Render() { current.Render = (Now - current.Start) * 1000; }
        internal void Gui(double before) { current.Gui += (Now - before) * 1000; }
        private void Write(Sample[] samples)
        {
            try {
                var text = new StringBuilder("frame,start,frame_ms,receiver_update_ms,late_start_ms,receiver_late_ms,pose_ms,render_start_ms,marker_ms,gen2,time_scale\n");
                foreach (var s in samples) text.AppendFormat(CultureInfo.InvariantCulture,
                    "{0},{1:F6},{2:F4},{3:F4},{4:F4},{5:F4},{6:F4},{7:F4},{8:F4},{9},{10}\n",
                    s.Frame,s.Start,s.Duration,s.Update,s.LateStart,s.Late,s.Pose,s.Render,s.Gui,s.Collections,s.Scale);
                File.WriteAllText(path, text.ToString());
            } catch (IOException) { } catch (UnauthorizedAccessException) { }
            finally { Interlocked.Exchange(ref writing, 0); }
        }
    }
}
