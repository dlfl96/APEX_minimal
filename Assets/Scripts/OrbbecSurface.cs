using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using Orbbec;
using UnityEngine;

// Femto Bolt (Orbbec SDK v2) als geschlossene, farbige OBERFLAECHE.
//
// Ablauf pro Kamerabild (Worker-Thread, Polling statt nativem Callback -> IL2CPP-sicher):
//   WaitForFrames -> AlignFilter (Farbe aufs Tiefengitter 640x576) -> PointCloudFilter (RGB-Punkte)
//   -> Gitter ausduennen (decimation) -> Tiefenfenster + Kantenschwelle -> Dreiecke.
// Update() laedt nur das jeweils neueste fertige Mesh hoch (aeltere werden verworfen).
//
// Koordinaten: Meter, Unity-Konvention (x rechts, y oben, z vom Sensor weg).
// Platzierung und Spiegelung ausschliesslich ueber den Transform.
[RequireComponent(typeof(MeshFilter), typeof(MeshRenderer))]
public class OrbbecSurface : MonoBehaviour
{
    [Tooltip("1 = volle Aufloesung (640x576). 2 = jede 2. Zeile/Spalte usw. Hoeher = fluessiger, aber groeber.")]
    [Range(1, 4)] public int decimation = 2;

    [Tooltip("Tiefenfenster (Meter vor dem Sensor): nur Punkte dazwischen werden zur Flaeche.")]
    public float nearMeters = 0.5f;
    public float farMeters = 3.0f;

    [Tooltip("Max. Tiefensprung (Meter) innerhalb einer Masche, bis zu dem noch Flaeche entsteht.")]
    public float edgeThresholdMeters = 0.05f;

    [Tooltip("Bildrate der Kamera (5, 15 oder 30). Wirkt nur beim Start.")]
    public int fps = 15;

    private const int DepthWidth = 640, DepthHeight = 576;   // NFOV unbinned
    private const int ColorWidth = 1280, ColorHeight = 720;
    private const int FloatsPerPoint = 6;                    // x, y, z, r, g, b

    private class MeshData
    {
        public Vector3[] vertices;
        public Color32[] colors;
        public int[] triangles;
        public int vertexCount, indexCount;
    }

    private Pipeline pipeline;
    private Config config;
    private AlignFilter align;
    private PointCloudFilter pointCloud;
    private bool pipelineStarted;

    private Thread worker;
    private volatile bool running;
    private float[] raw;

    // Doppelpuffer: der Worker schreibt in "back", Update liest "front"; getauscht wird unter Lock.
    private readonly object swapLock = new object();
    private MeshData back = new MeshData(), front = new MeshData();
    private bool frontReady;

    private Mesh mesh;
    private int uploadedVertexCount = -1;
    private bool loggedFirst;
    private static readonly Bounds FixedBounds = new Bounds(new Vector3(0f, 0f, 2f), Vector3.one * 10f);

    void Start()
    {
        mesh = new Mesh { name = "OrbbecSurface", indexFormat = UnityEngine.Rendering.IndexFormat.UInt32 };
        mesh.MarkDynamic();
        GetComponent<MeshFilter>().sharedMesh = mesh;

        string extensionsDir = Path.Combine(Application.streamingAssetsPath, "OrbbecSDK", "extensions");
        if (!File.Exists(Path.Combine(extensionsDir, "depthengine", "depthengine.dll")))
            Debug.LogError("OrbbecSurface: depthengine.dll fehlt unter " + extensionsDir + " -> die Femto Bolt liefert keine Tiefe.");

        try
        {
            // Muss vor dem ersten SDK-Objekt passieren. Log-Dateien nach persistentDataPath statt ins Projekt.
            Context.SetExtensionsDirectory(extensionsDir);
            Context.SetLoggerSeverity(LogSeverity.OB_LOG_SEVERITY_WARN);
            Context.SetLoggerToFile(LogSeverity.OB_LOG_SEVERITY_WARN, Path.Combine(Application.persistentDataPath, "OrbbecSDKLog"));

            pipeline = new Pipeline();
            config = new Config();
            config.EnableVideoStream(StreamType.OB_STREAM_DEPTH, DepthWidth, DepthHeight, fps, Format.OB_FORMAT_Y16);
            config.EnableVideoStream(StreamType.OB_STREAM_COLOR, ColorWidth, ColorHeight, fps, Format.OB_FORMAT_RGB);
            config.SetFrameAggregateOutputMode(FrameAggregateOutputMode.OB_FRAME_AGGREGATE_OUTPUT_ALL_TYPE_FRAME_REQUIRE);
            pipeline.EnableFrameSync();
            pipeline.Start(config);
            pipelineStarted = true;

            align = new AlignFilter(StreamType.OB_STREAM_DEPTH);   // Farbe -> Tiefengitter
            pointCloud = new PointCloudFilter();
            pointCloud.SetCreatePointFormat(Format.OB_FORMAT_RGB_POINT);
            pointCloud.SetCoordinateSystem(CoordinateSystemType.OB_LEFT_HAND_COORDINATE_SYSTEM); // y nach oben wie Unity
        }
        catch (Exception e)
        {
            Debug.LogError("OrbbecSurface: Start fehlgeschlagen. Kamera an USB 3? Orbbec Viewer geschlossen?\n" + e.Message);
            return;
        }

        running = true;
        worker = new Thread(Work) { IsBackground = true, Name = "OrbbecSurface" };
        worker.Start();
    }

    private void Work()
    {
        int errorCount = 0;
        while (running)
        {
            try
            {
                using var frames = pipeline.WaitForFrames(100);
                if (frames == null) continue;
                using var aligned = align.Process(frames);
                if (aligned == null) continue;
                using var cloud = pointCloud.Process(aligned);
                if (cloud == null) continue;
                using var points = cloud.As<PointsFrame>();
                if (points == null) continue;

                BuildMesh(points, back);
                lock (swapLock)
                {
                    (front, back) = (back, front);
                    frontReady = true;
                }
            }
            catch (Exception e)
            {
                if (++errorCount <= 5) Debug.LogError("OrbbecSurface: " + e.Message);
                Thread.Sleep(100);
            }
        }
    }

    private void BuildMesh(PointsFrame points, MeshData d)
    {
        int w = (int)points.GetWidth(), h = (int)points.GetHeight();
        int pointCount = w * h;
        if (points.GetDataSize() != pointCount * FloatsPerPoint * sizeof(float))
            throw new InvalidOperationException($"Punktwolke hat {points.GetDataSize()} Bytes, erwartet {w}x{h} Gitterpunkte.");

        if (raw == null || raw.Length != pointCount * FloatsPerPoint) raw = new float[pointCount * FloatsPerPoint];
        Marshal.Copy(points.GetDataPtr(), raw, 0, raw.Length);
        float toMeters = points.GetPositionValueScale() * 0.001f;

        int dec = Mathf.Clamp(decimation, 1, 4);
        float near = Mathf.Max(nearMeters, 0.01f), far = farMeters, edge = edgeThresholdMeters;
        int rw = (w - 1) / dec + 1, rh = (h - 1) / dec + 1, vertexCount = rw * rh;
        if (d.vertices == null || d.vertices.Length != vertexCount)
        {
            d.vertices = new Vector3[vertexCount];
            d.colors = new Color32[vertexCount];
            d.triangles = new int[(rw - 1) * (rh - 1) * 6];
        }

        // Gitter ausduennen: ungueltige Punkte kommen als (0,0,0) und fallen unten am Tiefenfenster raus.
        for (int ry = 0; ry < rh; ry++)
        {
            for (int rx = 0; rx < rw; rx++)
            {
                int s = (ry * dec * w + rx * dec) * FloatsPerPoint;
                int i = ry * rw + rx;
                d.vertices[i] = new Vector3(raw[s], raw[s + 1], raw[s + 2]) * toMeters;
                d.colors[i] = new Color32((byte)raw[s + 3], (byte)raw[s + 4], (byte)raw[s + 5], 255);
            }
        }

        // Je Masche zwei Dreiecke, wenn alle vier Ecken im Tiefenfenster liegen und kein Tiefensprung dazwischen ist.
        int ic = 0;
        for (int ry = 0; ry < rh - 1; ry++)
        {
            for (int rx = 0; rx < rw - 1; rx++)
            {
                int i0 = ry * rw + rx, i1 = i0 + 1, i2 = i0 + rw, i3 = i2 + 1;
                float z0 = d.vertices[i0].z, z1 = d.vertices[i1].z, z2 = d.vertices[i2].z, z3 = d.vertices[i3].z;
                float zMin = Mathf.Min(Mathf.Min(z0, z1), Mathf.Min(z2, z3));
                float zMax = Mathf.Max(Mathf.Max(z0, z1), Mathf.Max(z2, z3));
                if (zMin < near || zMax > far || zMax - zMin > edge) continue;
                d.triangles[ic++] = i0; d.triangles[ic++] = i2; d.triangles[ic++] = i1;
                d.triangles[ic++] = i1; d.triangles[ic++] = i2; d.triangles[ic++] = i3;
            }
        }
        d.vertexCount = vertexCount;
        d.indexCount = ic;

        if (!loggedFirst)
        {
            loggedFirst = true;
            Debug.Log($"OrbbecSurface: Punktwolke {w}x{h}, decimation {dec} -> Gitter {rw}x{rh}, {ic / 3} Dreiecke");
        }
    }

    void Update()
    {
        lock (swapLock)
        {
            if (!frontReady) return;
            frontReady = false;

            var d = front;
            var flags = UnityEngine.Rendering.MeshUpdateFlags.DontRecalculateBounds | UnityEngine.Rendering.MeshUpdateFlags.DontValidateIndices;
            if (d.vertexCount != uploadedVertexCount)
            {
                mesh.Clear(); // sonst verweisen alte Indizes auf nicht mehr vorhandene Vertices
                uploadedVertexCount = d.vertexCount;
            }
            mesh.SetVertices(d.vertices, 0, d.vertexCount, flags);
            mesh.SetColors(d.colors, 0, d.vertexCount, flags);
            mesh.SetIndices(d.triangles, 0, d.indexCount, MeshTopology.Triangles, 0, false);
            mesh.bounds = FixedBounds;
        }
    }

    void OnDestroy()
    {
        running = false;
        worker?.Join(1000);
        if (pipelineStarted)
        {
            try { pipeline.Stop(); }
            catch (Exception e) { Debug.LogWarning("OrbbecSurface: Stop: " + e.Message); }
        }
        pointCloud?.Dispose();
        align?.Dispose();
        config?.Dispose();
        pipeline?.Dispose();
        if (mesh != null) Destroy(mesh);
    }
}
