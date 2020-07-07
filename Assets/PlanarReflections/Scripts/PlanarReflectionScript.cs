﻿using UnityEngine;
using System;
using Unity.Burst;
using Unity.Collections;
using Unity.Mathematics;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using Unity.Entities;
using Unity.Jobs;
public class PlanarReflectionScript : MonoBehaviour
{
    //Local camera the script is attached to.
    private Camera _targetCamera;
    //Entity Variables
    private Camera _entityAttachedCam;
    private static EntityArchetype _cameraArchetype;
    private EntityManager _entityManager;
    //_fpsCounter used for frame skip option
    private int _fpsCounter;
    //Controls whether or not to contribute HDR elements (eg emission) to the reflection.
    private bool _currentHDRsetting;
    //Used for realtime adjustment of reflection resolution.
    private int _currentRenderTextureint;
    //Primary reflection texture.
    private  RenderTexture _reflTexture;
    //Reflection resolutions settings variables.
    public enum ResolutionMultipliers
    {
        Full,
        Half,
        Third,
        Quarter
    }
    private float GetScaleValue()
    {
        switch (planarLayerSettings.resolutionMultiplier)
        {
            case ResolutionMultipliers.Full:
                return 1f;
            case ResolutionMultipliers.Half:
                return 0.5f;
            case ResolutionMultipliers.Third:
                return 0.33f;
            case ResolutionMultipliers.Quarter:
                return 0.25f;
            default:
                return 0.5f;
        }
    }
//Instance of custom class used for reflection settings in the inspector.
    public PlanarReflectionSettings  planarLayerSettings = new PlanarReflectionSettings();
    [Serializable]
    public class PlanarReflectionSettings
    {
        public bool recursiveReflection;
        public int recursiveGroup = 1;
        public string shaderPropertyName;
        public float3 direction;
        public float clipPlaneOffset = 0.07f;
        public LayerMask reflectLayers = -1;
        public ResolutionMultipliers resolutionMultiplier;
        public bool shadows;
        public int frameSkip = 1;
        public bool occlusion;
        public bool addBlackColour;
        public bool enableHdr;
        public bool enableMSAA;
    }
    //Utility methods for killing reflections cleanly.
    private void OnDisable()
    {
        Cleanup();
    }
    private void OnDestroy()
    {
        Cleanup();
    }
    private void Cleanup()
    {
        RenderPipelineManager.beginCameraRendering -= ExecutePlanarReflections;
        if (!_reflTexture) return;
        RenderTexture.ReleaseTemporary(_reflTexture);
        _reflTexture = null;
    }
    private void Update()
    {
        _fpsCounter++;
    } 
    private void Start()
    {
        _entityManager = World.DefaultGameObjectInjectionWorld.EntityManager;
        _targetCamera = GetComponent<Camera>();
        _cameraArchetype = _entityManager.CreateArchetype(typeof(CamObjectStruct));
        if (!planarLayerSettings.recursiveReflection)
            RenderPipelineManager.beginCameraRendering += ExecutePlanarReflections;
    }
    private void ExecutePlanarReflections(ScriptableRenderContext arg1, Camera arg2)
    {
        if (planarLayerSettings.recursiveReflection)
            return;
        ExecuteRenderSequence(arg1);
    }
    public Camera ExecuteRenderSequence(ScriptableRenderContext src, Camera sentCamera = null
        , bool inverted = true, bool enableRender = true)
    { if (this != null)
        {
            var cameraToUse = sentCamera;
            if (cameraToUse == null && this.gameObject != null)
                cameraToUse = _targetCamera;
            if (cameraToUse != null && cameraToUse.cameraType == CameraType.Reflection)
                return null; 
            bool _skipFrame = _fpsCounter % planarLayerSettings.frameSkip != 0;
            if (_skipFrame)
            {
                return null;
            }
            else if (_fpsCounter > 1000)
            {
                _fpsCounter = 0;
            }
            var fogcache = RenderSettings.fog;
            RenderSettings.fog = false;
            CreateMirrorObjects(cameraToUse, out Camera reflectionCamera);
            UpdateCameraModes(cameraToUse, reflectionCamera);
            reflectionCamera.cullingMask = planarLayerSettings.reflectLayers;
            float3 normal = planarLayerSettings.direction;
            float d = -planarLayerSettings.clipPlaneOffset;
            float4 reflectionPlane = new float4(normal.x, normal.y, normal.z, d);
            Matrix4x4 reflection = Matrix4x4.identity;
            NativeArray<Matrix4x4> resultMatrix = new NativeArray<Matrix4x4>(1, Allocator.TempJob);
            CalculateReflectionMatrixJob calculateReflectionMatrix = new CalculateReflectionMatrixJob
            {
                reflectionMat = reflection, plane = reflectionPlane, ResultMatrix = resultMatrix
            };
            JobHandle handle = calculateReflectionMatrix.Schedule();
            NativeArray<float4> cameraSpacePlaneResult = new NativeArray<float4>(1, Allocator.TempJob);
            CameraSpacePlaneJob cameraSpacePlaneJob = new CameraSpacePlaneJob();
            cameraSpacePlaneJob.Normal = normal;
            cameraSpacePlaneJob.ResultMatrix = resultMatrix;
            cameraSpacePlaneJob.SideSign = inverted ? 1.0f : -1.0f;
            cameraSpacePlaneJob.OffsetPos = normal * planarLayerSettings.clipPlaneOffset;
            cameraSpacePlaneJob.WorldToCameraMatrix = cameraToUse.worldToCameraMatrix;
            cameraSpacePlaneJob.CameraSpacePlaneResult = cameraSpacePlaneResult;
            JobHandle cameraSpaceHandle = cameraSpacePlaneJob.Schedule(handle);
            Matrix4x4 projectionMatrix= cameraToUse.projectionMatrix;
            NativeArray<Matrix4x4> matrixtemp = new NativeArray<Matrix4x4>(1, Allocator.TempJob);
            MakeProjectionMatrixObliqueJob makeProjectionMatrixObliqueJob = new MakeProjectionMatrixObliqueJob();
            makeProjectionMatrixObliqueJob.Matrix = projectionMatrix;
            makeProjectionMatrixObliqueJob.Matrixtemp = matrixtemp;
            makeProjectionMatrixObliqueJob.cameraSpacePlaneResult = cameraSpacePlaneResult;
            JobHandle makeProjectionMatrixObliqueHandle = makeProjectionMatrixObliqueJob.Schedule(cameraSpaceHandle);
            makeProjectionMatrixObliqueHandle.Complete();
            Matrix4x4 worldToCameraMatrix =cameraToUse.worldToCameraMatrix * resultMatrix[0];
            reflectionCamera.transform.position = cameraToUse.transform.position;
            reflectionCamera.worldToCameraMatrix = worldToCameraMatrix;
            cameraSpacePlaneJob.CameraSpacePlaneResult = cameraSpacePlaneResult;
            projectionMatrix = matrixtemp[0];
            matrixtemp.Dispose();
            reflectionCamera.projectionMatrix = projectionMatrix;
            reflectionCamera.transform.rotation = cameraToUse.transform.rotation;
            var oldInvertCulling = GL.invertCulling;
            GL.invertCulling = inverted;
            reflectionCamera.targetTexture = _reflTexture;
            if (enableRender)
            {
                UniversalRenderPipeline.RenderSingleCamera(src, reflectionCamera);
            }
            GL.invertCulling = oldInvertCulling;
            if (enableRender)
            {
                UpdateShader();
            }
            RenderSettings.fog = fogcache;
            return reflectionCamera;
        }
        else
        {
            return null;
        }
    }
    public void UpdateShader()
    {
        Shader.SetGlobalTexture(planarLayerSettings.shaderPropertyName, _reflTexture); 
    }
    private static float SignCheck(float a)
    {
        if (a > 0.0f) return 1.0f;
        if (a < 0.0f) return -1.0f;
        return 0.0f;
    }
    private void UpdateCameraModes(Camera src, Camera dest)
    {
        if (dest == null)
            return;
        if (planarLayerSettings.addBlackColour)
        {
            dest.clearFlags = CameraClearFlags.Color;
            dest.backgroundColor = new Color(0, 0, 0, 1);
        }
        else
        {
            dest.clearFlags = src.clearFlags;
            dest.backgroundColor = src.backgroundColor;
        }
        if (dest.gameObject.TryGetComponent(out UniversalAdditionalCameraData camData))
        {
            camData.renderShadows = planarLayerSettings.shadows;
            dest.nearClipPlane = src.nearClipPlane;
            dest.farClipPlane = src.farClipPlane;
            dest.orthographic = src.orthographic;
            dest.fieldOfView = src.fieldOfView;
            dest.aspect = src.aspect;
            dest.orthographicSize = src.orthographicSize;
            dest.allowHDR = planarLayerSettings.enableHdr;
            dest.allowMSAA = planarLayerSettings.enableMSAA;
            dest.useOcclusionCulling = planarLayerSettings.occlusion;
        }
    }
    private int2 ReflectionResolution(Camera cam, float scale)
    {
        var x = (int) (cam.pixelWidth * scale * GetScaleValue());
        var y = (int) (cam.pixelHeight * scale * GetScaleValue());
        return new int2(x, y);
    }
    private void CreateMirrorObjects(Camera currentCamera, out Camera reflectionCamera)
    {
        var textureSize = ReflectionResolution(currentCamera, UniversalRenderPipeline.asset.renderScale);
        if (!_reflTexture ||
            planarLayerSettings.enableHdr != _currentHDRsetting
            || _currentRenderTextureint != textureSize[0] )
        {
            if (_reflTexture)
                RenderTexture.ReleaseTemporary(_reflTexture);
            if (planarLayerSettings.enableHdr)
                _reflTexture = RenderTexture.GetTemporary(textureSize[0], textureSize[1], 24, RenderTextureFormat.DefaultHDR);
            else
                _reflTexture =  RenderTexture.GetTemporary(textureSize[0], textureSize[1], 24, RenderTextureFormat.Default);
            if (QualitySettings.antiAliasing > 0)
                _reflTexture.antiAliasing = QualitySettings.antiAliasing;
            _currentRenderTextureint = textureSize[0];
            _currentHDRsetting = planarLayerSettings.enableHdr;
        }
        if (_entityAttachedCam != null)
        {
            reflectionCamera = _entityAttachedCam;
        }
        else
        {
            var query = _entityManager.CreateEntityQuery(typeof(CamObjectStruct)).ToEntityArray(Allocator.TempJob);
            if (query.Length == 0)
            {
                Entity camEntity = _entityManager.CreateEntity(_cameraArchetype);
                GameObject go = new GameObject();
                go.AddComponent<Camera>();
                var cameraData =
                    go.AddComponent(typeof(UniversalAdditionalCameraData)) as UniversalAdditionalCameraData;
                if (cameraData != null)
                {
                    cameraData.requiresColorOption = CameraOverrideOption.Off;
                    cameraData.requiresDepthOption = CameraOverrideOption.Off;
                    cameraData.SetRenderer(0);
                }
                var reflectionCam = go.GetComponent<Camera>();
                go.hideFlags = HideFlags.HideAndDontSave;
                _entityManager.SetComponentData(camEntity,
                    new CamObjectStruct
                    {
                        Cam = go.GetComponent<Camera>(),
                        Uacd = go.GetComponent<UniversalAdditionalCameraData>()
                    });
                Camera tempcam = _entityManager.GetComponentData<CamObjectStruct>(camEntity).Cam;
                reflectionCamera = tempcam;
                reflectionCamera.enabled = false;
                _entityAttachedCam = tempcam;
            }
            else
            {
                Camera tempcam = _entityManager
                    .GetComponentData<CamObjectStruct>(query[0]).Cam;
                reflectionCamera = tempcam;
                _entityAttachedCam = tempcam;
            } 
            query.Dispose();
        }
    }
    [BurstCompile(CompileSynchronously = false)]
    private struct MakeProjectionMatrixObliqueJob : IJob
    {
        public NativeArray<float4> cameraSpacePlaneResult;
        private float4 ClipPlane;
        public NativeArray<Matrix4x4> Matrixtemp;
        public Matrix4x4 Matrix;
        public void Execute()
        {
            ClipPlane = cameraSpacePlaneResult[0];
            float4 q;
            q.x = (SignCheck(ClipPlane.x) + Matrix[8]) / Matrix[0];
            q.y = (SignCheck(ClipPlane.y) + Matrix[9]) / Matrix[5];
            q.z = -1.0F;
            q.w = (1.05F + Matrix[10]) / Matrix[14];
            float4 c = ClipPlane * (2.0F / math.dot(ClipPlane, q));
            Matrix[2] = c.x;
            Matrix[6] = c.y;
            Matrix[10] = c.z + 1.0F;
            Matrix[14] = c.w;
            Matrixtemp[0] = Matrix;
        }
    }
    [BurstCompile(CompileSynchronously = false)]
    private struct CameraSpacePlaneJob : IJob
    {
        public NativeArray<Matrix4x4> ResultMatrix;
        public float3 OffsetPos;
        public float3 Normal;
        public Matrix4x4 WorldToCameraMatrix;
        public float SideSign;
        public NativeArray<float4> CameraSpacePlaneResult; 
        public void Execute()
        {
            WorldToCameraMatrix = WorldToCameraMatrix * ResultMatrix[0];
            float3 cameraPosition = WorldToCameraMatrix.MultiplyPoint(OffsetPos);
            float3 cameraNormal = WorldToCameraMatrix.MultiplyVector(Normal).normalized * SideSign;
            CameraSpacePlaneResult[0] = new float4(cameraNormal.x, cameraNormal.y, cameraNormal.z, -math.dot(cameraPosition, cameraNormal));
        }
    }
    [BurstCompile(CompileSynchronously = false)]
    private struct CalculateReflectionMatrixJob : IJob
    {
        public float4 plane;
        public Matrix4x4 reflectionMat;
        public NativeArray<Matrix4x4> ResultMatrix;
        public void Execute()
        {
            reflectionMat.m00 = 1F - 2F * plane[0] * plane[0];
            reflectionMat.m01 = -2F * plane[0] * plane[1];
            reflectionMat.m02 = -2F * plane[0] * plane[2];
            reflectionMat.m03 = -2F * plane[3] * plane[0];
            reflectionMat.m10 = -2F * plane[1] * plane[0];
            reflectionMat.m11 = 1F - 2F * plane[1] * plane[1];
            reflectionMat.m12 = -2F * plane[1] * plane[2];
            reflectionMat.m13 = -2F * plane[3] * plane[1];
            reflectionMat.m20 = -2F * plane[2] * plane[0];
            reflectionMat.m21 = -2F * plane[2] * plane[1];
            reflectionMat.m22 = 1F - 2F * plane[2] * plane[2];
            reflectionMat.m23 = -2F * plane[3] * plane[2];
            reflectionMat.m33 = 1F;
            ResultMatrix[0] = reflectionMat;
        }
    }
}
[Serializable]
public class CamObjectStruct : IComponentData
{ 
    public UniversalAdditionalCameraData Uacd {get;  set; } 
    public  Camera Cam {get;  set; }
}