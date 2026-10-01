using System;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEngine;
using UnityEngine.Rendering;

namespace WotRDLSS
{
    // Debug tool: records the DLSS inputs of a few consecutive frames (colour, final motion vectors, character motion target) plus the
    // DLSS output of the last one, as raw half-float files with a metadata file per frame, so the frames can be analysed offline
    // (for example by reprojecting one frame onto the next with the motion vectors).
    public static class Capture
    {
        public static string Status = "";
        static float startAt = -1f;
        static int framesLeft, index, pending;
        static string dir;
        const int Frames = 4;

        public static void Arm(float delaySeconds)
        {
            startAt = Time.unscaledTime + delaySeconds;
            framesLeft = Frames;
            index = 0;
            dir = Path.Combine(Main.Dir, "capture_" + DateTime.Now.ToString("yyyyMMdd_HHmmss"));
            Status = "armed, starts in " + delaySeconds.ToString("F0") + " s";
            Main.Log("capture armed: " + dir);
        }

        static string F(float v) { return v.ToString("R", CultureInfo.InvariantCulture); }

        static string M(Matrix4x4 m)
        {
            var sb = new StringBuilder("[");
            for (int r = 0; r < 4; r++) for (int c = 0; c < 4; c++) sb.Append(r + c == 0 ? "" : ",").Append(F(m[r, c]));
            return sb.Append("]").ToString();
        }

        // Called once per evaluated frame, after the motion vector merge and the DLSS evaluate were queued on cb.
        public static void Queue(CommandBuffer cb, RenderTexture color, RenderTexture motion, RenderTexture obj, RenderTexture full, int rw, int rh, bool reset)
        {
            if (startAt < 0f || Time.unscaledTime < startAt || framesLeft <= 0) return;
            if (index == 0) Directory.CreateDirectory(dir);
            int i = index++;
            framesLeft--;
            bool last = framesLeft == 0;
            if (last) startAt = -1f;

            var cam = Camera.main;
            var meta = new StringBuilder();
            meta.Append("{\n");
            meta.Append("\"index\":").Append(i).Append(",\n\"frame\":").Append(Time.frameCount).Append(",\n");
            meta.Append("\"render\":[").Append(rw).Append(",").Append(rh).Append("],\n\"output\":[").Append(full.width).Append(",").Append(full.height).Append("],\n");
            meta.Append("\"jitterPx\":[").Append(F(Jitter.JitterPx.x)).Append(",").Append(F(Jitter.JitterPx.y)).Append("],\n");
            meta.Append("\"jitterSign\":[").Append(F(Main.S.jitSx)).Append(",").Append(F(Main.S.jitSy)).Append("],\n");
            meta.Append("\"mvSign\":[").Append(F(Main.S.mvSignX)).Append(",").Append(F(Main.S.mvSignY)).Append("],\n");
            meta.Append("\"objSign\":[").Append(F(Main.S.objSignX)).Append(",").Append(F(Main.S.objSignY)).Append("],\n");
            meta.Append("\"reset\":").Append(reset ? "true" : "false").Append(",\n");
            meta.Append("\"beforePost\":").Append(Main.S.dlssBeforePost ? "true" : "false").Append(",\n");
            meta.Append("\"preset\":").Append(Main.S.preset).Append(",\n");
            meta.Append("\"time\":").Append(F(Time.unscaledTime)).Append(",\n\"deltaTime\":").Append(F(Time.unscaledDeltaTime)).Append(",\n");
            if (cam != null) meta.Append("\"camPos\":[").Append(F(cam.transform.position.x)).Append(",").Append(F(cam.transform.position.y)).Append(",").Append(F(cam.transform.position.z)).Append("],\n");
            meta.Append("\"vpCurrent\":").Append(M(Jitter.VPCurrent)).Append(",\n");
            meta.Append("\"vpPrevious\":").Append(M(Jitter.VPPrevious)).Append(",\n");
            meta.Append("\"vpJittered\":").Append(M(Jitter.VPJittered)).Append("\n}\n");
            File.WriteAllText(Path.Combine(dir, "f" + i + "_meta.json"), meta.ToString());

            Request(cb, color, "f" + i + "_color");
            Request(cb, motion, "f" + i + "_mv");
            if (obj != null) Request(cb, obj, "f" + i + "_obj");
            Request(cb, full, "f" + i + "_out");
            Status = last ? "frames queued, writing files" : "capturing";
        }

        static void Request(CommandBuffer cb, RenderTexture rt, string name)
        {
            int w = rt.width, h = rt.height;
            int ch = rt.format == RenderTextureFormat.RGHalf ? 2 : 4;
            string path = Path.Combine(dir, name + "_" + w + "x" + h + "_" + ch + "h.raw");
            pending++;
            cb.RequestAsyncReadback(rt, req =>
            {
                try
                {
                    if (req.hasError) Main.Log("capture readback failed: " + name);
                    else File.WriteAllBytes(path, req.GetData<byte>().ToArray());
                }
                catch (Exception e) { Main.Log("capture write failed " + name + ": " + e.Message); }
                if (--pending == 0 && startAt < 0f) { Status = "done: " + dir; Main.Log("capture written: " + dir); }
            });
        }
    }
}
