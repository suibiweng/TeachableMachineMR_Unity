using System;
using UnityEngine;

public class OnlineCentroidTrainer
{
    private int C, D;
    private float[][] sums; // [C][D]
    private int[] counts;

    public OnlineCentroidTrainer(int numClasses, int dim) {
        C = numClasses; D = dim;
        sums = new float[C][]; counts = new int[C];
        for (int c=0;c<C;c++) { sums[c] = new float[D]; counts[c]=0; }
    }

    public void AddSample(int cls, float[] z) {
        var zz = (float[])z.Clone();
        TinyHeads.L2Normalize(zz);
        for (int i=0;i<D;i++) sums[cls][i] += zz[i];
        counts[cls]++;
    }

    public int GetCount(int cls) => counts[cls];

    public HeadData ToHeadData(string[] classNames) {
        var head = new HeadData { type="centroid", classes = classNames, centroids = new float[C][] };
        for (int c=0;c<C;c++) {
            head.centroids[c] = new float[D];
            if (counts[c] > 0) {
                for (int i=0;i<D;i++) head.centroids[c][i] = sums[c][i] / counts[c];
                TinyHeads.L2Normalize(head.centroids[c]);
            }
        }
        return head;
    }
}
