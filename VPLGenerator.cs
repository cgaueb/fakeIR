using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEditor;
using System;
using UnityEngine.SceneManagement;

public class VPL 
{
    public static float m_spacing = 1.0f;
    public Vector3 m_pos;
    public Vector3 m_dir;
    public Color m_color;
    public float m_intensity;
    public float m_range;
    public int m_cluster_population = 1;
    // since the VPL here represents a VPL cluster,
    // the cluster's representative population is maintained
    // for correct averaging.
}

class VPLComparator : IEqualityComparer<VPL>
{
    public bool Equals(VPL v1, VPL v2)
    {
        if (ReferenceEquals(v1, v2))
            return true;

        if (v2 is null || v1 is null)
            return false;

        Vector3 vv = v1.m_pos - v2.m_pos;
        return (
             (Vector3.Dot(vv, vv) < VPL.m_spacing * VPL.m_spacing) &&
             (Vector3.Dot(v1.m_dir, v2.m_dir) > 0.707f) // 45 deg
            );
            
    }

    public int GetHashCode(VPL v)
    {
        int a = (int)(MathF.Round(v.m_dir.x + 1.0f)) + 1;
        int b = (int)(MathF.Round(v.m_dir.y + 1.0f)) + 1;
        int c = (int)(MathF.Round(v.m_dir.z + 1.0f)) + 1;

        return 100 *a + 10*b + c;   
    }
}

public class VPLGenerator : EditorWindow
{
    private int _explorationPoints = 30;
    private int _maxSpawnedVPLs = 500;
    private int _maxNumVPLs = 20;
    private int _maxLevel = 3;
    private float _spacing = 2.0f;
    private float _offset = 0.0f;
    private bool _useMeshCollisions = false;
    private GameObject _focus;

    HashSet<VPL> m_vpls = new HashSet<VPL>(new VPLComparator());
    List<GameObject> m_modified_meshes = new List<GameObject>();


    //gui stuff
    bool _showAdvancedOptions = false;


    protected HashSet<VPL> TraceFeeler(Vector3 pos, Vector3 dir, int level = 0)
    // Recursively trace a path to generate VPLs on each hit point
    {
        RaycastHit hit;
        HashSet<VPL> vpls = new HashSet<VPL>(new VPLComparator());

        if (level > _maxLevel)
            return vpls;

        if (!Physics.Raycast(pos, dir, out hit))
        {
            return vpls;
        }

        VPL v = new VPL();
        v.m_pos = hit.point;

        MeshCollider meshCollider = hit.collider as MeshCollider;
        if (meshCollider != null && meshCollider.sharedMesh != null)
        // If a mesh collider hit, interpolate collision normal from 
        // barycentric coordinates and shading normals
        {
            Mesh mesh = meshCollider.sharedMesh;
            Vector3 baryCenter = hit.barycentricCoordinate;

            Vector3[] normals = mesh.normals;
            int[] triangles = mesh.triangles;

            Vector3 n0 = normals[triangles[hit.triangleIndex * 3 + 0]];
            Vector3 n1 = normals[triangles[hit.triangleIndex * 3 + 1]];
            Vector3 n2 = normals[triangles[hit.triangleIndex * 3 + 2]];

            Vector3 interpolatedNormal = n0 * baryCenter.x + n1 * baryCenter.y + n2 * baryCenter.z;
            interpolatedNormal = interpolatedNormal.normalized;

            Transform hitTransform = hit.collider.transform;
            
            v.m_dir = hitTransform.TransformDirection(interpolatedNormal);
        }
        else
        {
            v.m_dir = hit.normal;
        }

        // Try to sample the surface color
        Renderer rend = hit.transform.GetComponent<Renderer>();
        if (rend != null && rend.sharedMaterial != null)
        {
            v.m_color = rend.sharedMaterial.color;
            
            if (rend.sharedMaterial.mainTexture != null && rend.sharedMaterial.mainTexture.isReadable)
            {
                // sample texture
                Texture2D tex = rend.sharedMaterial.mainTexture as Texture2D;
                Vector2 pixelUV = hit.textureCoord;
                v.m_color *= tex.GetPixelBilinear(pixelUV.x, pixelUV.y);
            }
        }
        else
        {
            // No material or texture detected
            v.m_color = new Color(0.95f, 0.95f, 0.95f);
        }
        

        v.m_intensity = 1.0f;
        v.m_range = hit.distance;
        
        VPL found;
        if (vpls.TryGetValue(v, out found))
        // If an overlap with another VPL is found,
        // cluster them
        {
            float w_old = found.m_cluster_population / (found.m_cluster_population + 1.0f);
            float w_new = 1.0f / (found.m_cluster_population + 1.0f);

            VPL newvpl = new VPL();
            newvpl.m_pos = w_old * found.m_pos + w_new * v.m_pos;
            newvpl.m_dir = Vector3.Normalize(w_old * found.m_dir + w_new * v.m_dir);
            newvpl.m_intensity = w_old * found.m_intensity + w_new * v.m_intensity;
            newvpl.m_color = w_old * found.m_color + w_new * v.m_color;
            newvpl.m_range = w_old * found.m_range + w_new * v.m_range;
            newvpl.m_cluster_population = found.m_cluster_population + 1;
            vpls.Remove(found);
            vpls.Add(newvpl);
        }
        else
        {
            vpls.Add(v);
        }
        
        
        Vector3 new_dir = UnityEngine.Random.onUnitSphere;
        if (Vector3.Dot(new_dir, v.m_dir) < 0.0f)
            new_dir *= -1.0f;

        vpls.UnionWith(TraceFeeler(pos + v.m_dir * 0.001f, new_dir, level + 1));

        return vpls;
    }
    
    protected void PreProcess()
    // Scan the scene for meshes and temporarily add or replace any colliders 
    // with a MeshCollider, when the more accurate collision option is set. 
    // No effect, otherwise.
    {
        if (!_useMeshCollisions)
            return;

        UnityEngine.Object[] obj = UnityEngine.Object.FindObjectsOfType(typeof(GameObject));
        foreach (UnityEngine.Object o in obj)
        {
            GameObject go = (GameObject)o;
            
            if (go!=null && go.GetComponent<MeshFilter>()!=null && go.GetComponent<MeshFilter>().sharedMesh != null)
            {
                MeshCollider collider = go.GetComponent<MeshCollider>();
                if (!collider)
                {
                    go.AddComponent<MeshCollider>();
                    m_modified_meshes.Add(go);
                }
            }
        }
    }

    protected void PostProcess()
    // Restore collision components, if changed during the VPL generation.
    {
        if (!_useMeshCollisions)
            return;

        foreach (GameObject o in m_modified_meshes)
        {
            if (o!= null && o.GetComponent<MeshCollider>() != null)
            DestroyImmediate(o.GetComponent<MeshCollider>());
        }
    }

    protected void Generate(Vector3 pos)
    {
        PreProcess();

        System.Random rnd = new System.Random();
        
        Vector3[] points = new Vector3[_explorationPoints];
        points[0] = pos;
        int next_point = 1;

        // Mutate the starting reference point for path tracing
        // in order to explore the usable space more effectively.
        for (int k = 0; k < _explorationPoints; k++)
        {
            Vector3 cur_pos = points[rnd.Next(next_point)];
            RaycastHit hit;
            Vector3 dir = UnityEngine.Random.onUnitSphere;

            if (!Physics.Raycast(cur_pos, dir, out hit))
                continue;

            points[next_point++] = cur_pos + dir * hit.distance / 2.0f;
        }

        // Trace VPL paths
        for (int k = 0; k < next_point; k++)
        {
            for (int i = 0; i < _maxSpawnedVPLs / (_maxLevel * next_point); i++)
            {
                m_vpls.UnionWith(TraceFeeler(points[k], UnityEngine.Random.onUnitSphere));
                
            }
        }

        PostProcess();
    }

    protected void FilterVPLs()
    // Cull VPLs to reach the desired maximum VPL number.
    {
        while (m_vpls.Count>_maxNumVPLs)
        {
            foreach (var vpl in m_vpls)
            {
                if (UnityEngine.Random.value < 0.1f)
                {
                    m_vpls.Remove(vpl);
                    break;
                }
            }
        }
    }

    protected List<GameObject> GetLights()
    // Create the scene lights for the VPLs
    {
        GameObject group = new GameObject();
        group.name = "VPLS";

        List<GameObject> lights = new List<GameObject>();
        int count = 0;
        foreach (VPL vpl in m_vpls)
        {
            GameObject light = new GameObject();
            light.transform.forward = vpl.m_dir;
            light.transform.position = vpl.m_pos - _offset * vpl.m_dir;
            light.transform.SetParent(group.transform);
            light.AddComponent<Light>();
            Light l = light.GetComponent<Light>();
            l.type = LightType.Spot;
            l.innerSpotAngle = 0;
            l.spotAngle = 160;
            l.name = "VPL" + count++;
            l.range = vpl.m_range;
            l.enabled = false;
            l.intensity = vpl.m_intensity;
            l.color = vpl.m_color;
        }
        return lights;
    }



    public void OnEnable()
    {
        _focus = SceneManager.GetActiveScene().GetRootGameObjects()[0];
    }

    // Attach the utility as a separate top menu entry.
    [MenuItem("VPLs/Generate Static VPLs")]
    public static void ShowWindow()
    {
        
        GetWindow<VPLGenerator>("Static VPL Generator");
    }

    private void OnGUI()
    {
        EditorGUILayout.Space(6);
        GUILayout.Label("Properties");
        _maxNumVPLs = EditorGUILayout.DelayedIntField("Max number of VPLs", _maxNumVPLs, GUI.skin.textArea);
        _spacing = EditorGUILayout.DelayedFloatField("Intended VPL spacing", _spacing, GUI.skin.textArea);
        VPL.m_spacing = _spacing;

        GUILayout.Label("Generate VPLs based on seed game object: ");
        GameObject obj = (GameObject)EditorGUILayout.ObjectField(_focus, typeof(GameObject), true);
        if (obj != null)
            _focus = obj;
        _useMeshCollisions = EditorGUILayout.Toggle("Use mesh collisions", _useMeshCollisions);

        EditorGUILayout.Space(6);
        _showAdvancedOptions = EditorGUILayout.BeginFoldoutHeaderGroup(_showAdvancedOptions, "Advanced options");

        if (_showAdvancedOptions)
        {
            _offset = EditorGUILayout.DelayedFloatField("VPL (negative) offset", _offset, GUI.skin.textArea);
            _maxLevel = EditorGUILayout.DelayedIntField("Max trace level for VPLs", _maxLevel, GUI.skin.textArea);
        }

        EditorGUILayout.EndFoldoutHeaderGroup();

        if (GUI.Button(new Rect(30, _showAdvancedOptions?190:150, 200, 40), new GUIContent("Generate VPLs")))
        {
            _maxSpawnedVPLs = _explorationPoints * _maxLevel * 10;
            
            if (_focus==null)
            {
                EditorUtility.DisplayDialog("Error", "Seed game object cannot be empty.", "Back");
                return;
            }

            Generate(_focus.transform.position);
            FilterVPLs();
            GetLights();

        }

        

    }
}
