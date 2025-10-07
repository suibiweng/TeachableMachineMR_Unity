using System;
using UnityEngine;

[Serializable]
public class HeadData {
    public string type = "centroid";   // "centroid" | "linear"
    public string[] classes;           // class names
    public float[][] centroids;        // [C][D] for centroid
    public float[,] W;                 // [D,C] for linear
}

public static class TinyHeads
{
    public static void L2Normalize(float[] v) {
        if (v == null || v.Length == 0) return;
        double s = 0.0; for (int i=0;i<v.Length;i++) s += (double)v[i]*v[i];
        float inv = (float)(1.0 / (Math.Sqrt(s) + 1e-9));
        for (int i=0;i<v.Length;i++) v[i] *= inv;
    }

    public static float CosSim(float[] a, float[] b) {
        int d = Mathf.Min(a?.Length ?? 0, b?.Length ?? 0);
        if (d == 0) return -999f;
        double dot = 0.0;
        for (int i=0;i<d;i++) dot += (double)a[i]*b[i];
        return (float)dot;
    }

    // SAFE version with null/shape checks
    public static (int, float) PredictCentroid(float[] z, HeadData head) {
        if (z == null || head == null || head.centroids == null || head.classes == null)
            return (-1, 0f);
        if (head.centroids.Length == 0 || head.classes.Length == 0 || head.centroids.Length != head.classes.Length)
            return (-1, 0f);

        L2Normalize(z);

        int best = -1; float bestS = -999f;
        for (int c = 0; c < head.centroids.Length; c++) {
            var cvec = head.centroids[c];
            if (cvec == null || cvec.Length == 0) continue;
            // If dims mismatch, compare over the min length
            int d = Mathf.Min(z.Length, cvec.Length);
            double dot = 0.0;
            for (int i=0;i<d;i++) dot += (double)cvec[i] * z[i];
            float s = (float)dot;
            if (s > bestS) { bestS = s; best = c; }
        }
        return (best, bestS);
    }

    public static (int, float) PredictLinear(float[] z, HeadData head) {
        if (z == null || head == null || head.W == null || head.classes == null || head.classes.Length == 0)
            return (-1, 0f);

        int D = z.Length;
        int C = head.classes.Length;
        double maxLogit = double.NegativeInfinity;
        double[] logits = new double[C];

        for (int c=0;c<C;c++) {
            double s=0.0;
            for (int d=0; d<D && d<head.W.GetLength(0); d++) s += (double)z[d] * (double)head.W[d,c];
            logits[c] = s;
            if (s > maxLogit) maxLogit = s;
        }
        double sum=0.0;
        for (int c=0;c<C;c++) { logits[c] = Math.Exp(logits[c]-maxLogit); sum += logits[c]; }
        int best=0; double bestP = logits[0]/sum;
        for (int c=1;c<C;c++) {
            double p = logits[c]/sum;
            if (p > bestP) { bestP = p; best = c; }
        }
        return (best, (float)bestP);
    }
}
