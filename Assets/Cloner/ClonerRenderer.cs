// Cloner - An example of use of procedural instancing.
// https://github.com/keijiro/Cloner

using UnityEngine;
using UnityEngine.Timeline;
using Klak.Chromatics;

namespace Cloner
{
    [ExecuteInEditMode]
    public sealed class ClonerRenderer : MonoBehaviour, ITimeControl
    {
        #region Point source properties

        [SerializeField] PointCloud _pointSource;

        public PointCloud pointSource {
            get { return _pointSource; }
            set { _pointSource = value; ReallocateBuffer(); }
        }

        #endregion

        #region Template properties

        [SerializeField] Mesh _template;

        public Mesh template {
            get { return _template; }
            set { _template = value; ReallocateBuffer(); }
        }

        [SerializeField] float _templateScale = 0.05f;

        public float templateScale {
            get { return _templateScale; }
            set { _templateScale = value; }
        }

        [SerializeField] float _scaleByNoise = 0.1f;

        public float scaleByNoise {
            get { return _scaleByNoise; }
            set { _scaleByNoise = value; }
        }

        [SerializeField] float _scaleByPulse = 0.1f;

        public float scaleByPulse {
            get { return _scaleByPulse; }
            set { _scaleByPulse = value; }
        }

        #endregion

        #region Noise field properties

        [SerializeField] float _noiseFrequency = 1;

        public float noiseFrequency {
            get { return _noiseFrequency; }
            set { _noiseFrequency = value; }
        }

        [SerializeField] Vector3 _noiseMotion = Vector3.up * 0.25f;

        public Vector3 noiseMotion {
            get { return _noiseMotion; }
            set { _noiseMotion = value; }
        }

        [SerializeField, Range(0, 1)] float _normalModifier = 0.125f;

        public float normalModifier {
            get { return _normalModifier; }
            set { _normalModifier = value; }
        }

        #endregion

        #region Pulse noise properties

        [SerializeField, Range(0, 0.1f)] float _pulseProbability = 0;

        public float pulseProbability {
            get { return _pulseProbability; }
            set { _pulseProbability = value; }
        }

        [SerializeField] float _pulseFrequency = 2;

        public float pulseFrequency {
            get { return _pulseFrequency; }
            set { _pulseFrequency = value; }
        }

        #endregion

        #region Material properties

        [SerializeField] Material _material;

        public Material material {
            get { return _material; }
            set { _material = value; }
        }

        [SerializeField] CosineGradient _gradient;

        public CosineGradient gradient {
            get { return _gradient; }
            set { _gradient = value; }
        }

        #endregion

        #region Misc properties

        [SerializeField] Bounds _bounds =
            new Bounds(Vector3.zero, Vector3.one * 10);

        public Bounds bounds {
            get { return _bounds; }
            set { _bounds = value; }
        }

        [SerializeField] int _randomSeed;

        public int randomSeed {
            get { return _randomSeed; }
            set { _randomSeed = value; }
        }

        #endregion

        #region Hidden attributes

        [SerializeField, HideInInspector] ComputeShader _compute;

        #endregion

        #region Private members

        ComputeBuffer _drawArgsBuffer;
        ComputeBuffer _positionBuffer;
        ComputeBuffer _normalBuffer;
        ComputeBuffer _tangentBuffer;
        ComputeBuffer _transformBuffer;

        Material _tempMaterial;
        MaterialPropertyBlock _props;

        float _time;
        bool _timeControlled;

        Bounds TransformedBounds {
            get {
                return new Bounds(
                    transform.TransformPoint(_bounds.center),
                    Vector3.Scale(transform.lossyScale, _bounds.size)
                );
            }
        }

        void ReallocateBuffer()
        {
            if (_drawArgsBuffer != null)
            {
                _drawArgsBuffer.Release();
                _drawArgsBuffer = null;

                _positionBuffer.Release();
                _positionBuffer = null;

                _normalBuffer.Release();
                _normalBuffer = null;

                _tangentBuffer.Release();
                _tangentBuffer = null;

                _transformBuffer.Release();
                _transformBuffer = null;
            }
        }

        #endregion

        #region Compute configurations

        const int kThreadCount = 64;

        int ThreadGroupCount {
            get { return Mathf.Max(1, _pointSource.pointCount / kThreadCount); }
        }

        int InstanceCount {
            get { return ThreadGroupCount * kThreadCount; }
        }

        #endregion

        #region MonoBehaviour functions

        void OnValidate()
        {
            _noiseFrequency = Mathf.Max(0, _noiseFrequency);
            _pulseFrequency = Mathf.Max(0, _pulseFrequency);
            _bounds.size = Vector3.Max(Vector3.zero, _bounds.size);
        }

        void OnDisable()
        {
            // Release the compute buffers here not in OnDestroy because that's
            // too late to avoid compute buffer leakage warnings.
            ReallocateBuffer();
        }

        void OnDestroy()
        {
            if (_tempMaterial)
            {
                if (Application.isPlaying)
                    Destroy(_tempMaterial);
                else
                    DestroyImmediate(_tempMaterial);
            }
        }

        void Update()
        {
            if (_pointSource == null || _template == null ||
                _material == null || _gradient == null) return;

            // Lazy initialization.
            if (_drawArgsBuffer == null)
            {
                // Initialize the indirect draw args buffer.
                _drawArgsBuffer = new ComputeBuffer(
                    1, 5 * sizeof(uint), ComputeBufferType.IndirectArguments
                );

                _drawArgsBuffer.SetData(new uint[5] {
                    _template.GetIndexCount(0), (uint)InstanceCount, 0, 0, 0
                });

                // Allocate compute buffers.
                _positionBuffer = _pointSource.CreatePositionBuffer();
                _normalBuffer = _pointSource.CreateNormalBuffer();
                _tangentBuffer = _pointSource.CreateTangentBuffer();
                _transformBuffer = new ComputeBuffer(InstanceCount * 3, 4 * 4);
            }

            // Use a cloned material to avoid issue 914787 ("Only one shadow is
            // cast when using Graphics.DrawMeshInstancedIndirect more than one
            // time per frame").
            // FIXME: remove this when issue 914787 gets fixed.
            if (_tempMaterial == null)
                _tempMaterial = new Material(_material);
            else
                _tempMaterial.CopyPropertiesFromMaterial(_material);

            // Calculate the time-based parameters.
            var noiseOffset = Vector3.one * _randomSeed + _noiseMotion * _time;
            var pulseTime = _pulseFrequency * (_time + _randomSeed);

            // Invoke the update compute kernel.
            var kernel = _compute.FindKernel("ClonerUpdate");

            _compute.SetInt("InstanceCount", InstanceCount);
            _compute.SetFloat("RcpInstanceCount", 1.0f / InstanceCount);

            _compute.SetFloat("BaseScale", _templateScale);
            _compute.SetFloat("ScaleNoise", _scaleByNoise);
            _compute.SetFloat("ScalePulse", _scaleByPulse);

            _compute.SetFloat("NoiseFrequency", _noiseFrequency);
            _compute.SetVector("NoiseOffset", noiseOffset);
            _compute.SetFloat("NormalModifier", _normalModifier);

            _compute.SetFloat("PulseProbability", _pulseProbability);
            _compute.SetFloat("PulseTime", pulseTime);

            _compute.SetBuffer(kernel, "PositionBuffer", _positionBuffer);
            _compute.SetBuffer(kernel, "NormalBuffer", _normalBuffer);
            _compute.SetBuffer(kernel, "TangentBuffer", _tangentBuffer);
            _compute.SetBuffer(kernel, "TransformBuffer", _transformBuffer);

            _compute.Dispatch(kernel, ThreadGroupCount, 1, 1);

            // Draw the template mesh with instancing.
            if (_props == null) _props = new MaterialPropertyBlock();

            _props.SetVector("_GradientA", _gradient.coeffsA);
            _props.SetVector("_GradientB", _gradient.coeffsB);
            _props.SetVector("_GradientC", _gradient.coeffsC2);
            _props.SetVector("_GradientD", _gradient.coeffsD2);

            _props.SetMatrix("_LocalToWorld", transform.localToWorldMatrix);
            _props.SetMatrix("_WorldToLocal", transform.worldToLocalMatrix);

            _props.SetBuffer("_TransformBuffer", _transformBuffer);
            _props.SetFloat("_InstanceCount", InstanceCount);

            Graphics.DrawMeshInstancedIndirect(
                _template, 0, _tempMaterial, TransformedBounds,
                _drawArgsBuffer, 0, _props
            );

            // Advance the time.
            if (!_timeControlled && Application.isPlaying)
                _time += Time.deltaTime;
        }

        void OnDrawGizmos()
        {
            Gizmos.color = new Color(0, 1, 1, 0.3f);
            Gizmos.matrix = transform.localToWorldMatrix;
            Gizmos.DrawWireCube(_bounds.center, _bounds.size);
        }

        void OnDrawGizmosSelected()
        {
            Gizmos.color = Color.yellow;
            Gizmos.matrix = transform.localToWorldMatrix;
            Gizmos.DrawWireCube(_bounds.center, _bounds.size);
        }

        #endregion

        #region ITimeControl functions

        public void OnControlTimeStart()
        {
            _timeControlled = true;
        }

        public void OnControlTimeStop()
        {
            _timeControlled = false;
        }

        public void SetTime(double time)
        {
            _time = (float)time;
        }

        #endregion
    }
}
