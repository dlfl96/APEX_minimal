using G3D;
using UnityEngine;

// Showcase-Schleife: Startansicht halten -> langsam um einen Drehpunkt schwenken und dabei rauszoomen
// -> kurz halten -> zurueck.
//
// Dreht den eigenen Transform (Position UND Blickrichtung) um die Welt-Hochachse durch den Drehpunkt.
// Gedacht fuer die Main Camera: die G3D-Teilkameras haengen als Kinder dran und laufen mit.
// Jede Pose wird aus der Startpose berechnet -> keine Drift, egal wie lange die Schleife laeuft.
//
// Laeuft vor der G3DCamera (Execution Order), weil die in ihrem Update() FOV und Fokus der Main Camera
// auf die Teilkameras uebertraegt -> kein Frame Versatz.
[DefaultExecutionOrder(-100)]
public class OrbitLoop : MonoBehaviour
{
    [Tooltip("Drehpunkt (z.B. leeres GameObject in der Mitte der Punktwolke). Leer = Punkt 'pivotDistance' vor der Startpose.")]
    public Transform pivot;

    [Tooltip("Abstand des Drehpunkts vor der Kamera, falls kein Pivot gesetzt ist. 2 m = focusDistance der G3DCamera.")]
    public float pivotDistance = 2f;

    [Tooltip("Schwenkwinkel in Grad. Negativ = andere Seite.")]
    public float angle = 90f;

    [Header("Zeiten (Sekunden)")]
    [Tooltip("Sekunden in der Startansicht.")]
    public float holdStart = 30f;

    [Tooltip("Sekunden fuer einen Schwenk (hin bzw. zurueck).")]
    public float moveDuration = 12f;

    [Tooltip("Sekunden in der seitlichen Ansicht.")]
    public float holdSide = 3f;

    [Header("Zoom (voll erreicht bei vollem Schwenkwinkel)")]
    [Tooltip("Sichtfeld (Grad, vertikal) in der seitlichen Ansicht. Groesser = mehr von der Punktwolke sichtbar. " +
             "Die Fokusebene der G3DCamera bleibt dabei auf dem Drehpunkt.")]
    [Range(1f, 120f)] public float sideFieldOfView = 30f;

    [Tooltip("Zusaetzlicher Abstand (Meter), um den die Kamera in der seitlichen Ansicht zurueckfaehrt. " +
             "Die Fokusdistanz der G3DCamera waechst mit, damit die Punktwolke in der Fokusebene bleibt.")]
    [Min(0f)] public float sideExtraDistance = 0f;

    private Vector3 startPos, pivotPoint;
    private Quaternion startRot;
    private Camera cam;
    private G3DCamera g3d;
    private float startFov, startFocus;
    private float t;

    void Start()
    {
        startPos = transform.position;
        startRot = transform.rotation;
        pivotPoint = pivot != null ? pivot.position : startPos + startRot * Vector3.forward * pivotDistance;

        cam = GetComponent<Camera>();
        if (cam != null) startFov = cam.fieldOfView;
        g3d = GetComponent<G3DCamera>();
        if (g3d != null) startFocus = g3d.focusDistance;
    }

    void Update()
    {
        float move = Mathf.Max(moveDuration, 0.01f);
        float cycle = holdStart + move + holdSide + move;
        t = (t + Time.deltaTime) % cycle;

        // 0 = Startansicht, 1 = voll geschwenkt; SmoothStep laesst weich an- und auslaufen.
        float k;
        if (t < holdStart) k = 0f;
        else if (t < holdStart + move) k = Mathf.SmoothStep(0f, 1f, (t - holdStart) / move);
        else if (t < holdStart + move + holdSide) k = 1f;
        else k = Mathf.SmoothStep(1f, 0f, (t - holdStart - move - holdSide) / move);

        Quaternion q = Quaternion.AngleAxis(angle * k, Vector3.up);
        Quaternion rot = q * startRot;
        float back = sideExtraDistance * k;
        transform.SetPositionAndRotation(pivotPoint + q * (startPos - pivotPoint) - rot * Vector3.forward * back, rot);

        if (cam != null) cam.fieldOfView = Mathf.Lerp(startFov, sideFieldOfView, k);
        if (g3d != null && sideExtraDistance > 0f) g3d.updateFocusDistance(startFocus + back);
    }
}
