/** 
 * Unity script for implementing fake GI effects, based on the article:
 * G. Papaioannou, Fake Dynamic Global Illumination for VR, [DETAILS MISSING]
 * 
 * Author: Georgios Papaioannou
 * 
 * Copyright 2024 Georgios Papaioannou
 * 
 * MIT License
 * 
 * Permission is hereby granted, free of charge, to any person obtaining a copy
 * of this software and associated documentation files (the “Software”), to deal 
 * in the Software without restriction, including without limitation the rights 
 * to use, copy, modify, merge, publish, distribute, sublicense, and/or sell copies 
 * of the Software, and to permit persons to whom the Software is furnished to do so, 
 * subject to the following conditions:
 * 
 * The above copyright notice and this permission notice shall be included in all
 * copies or substantial portions of the Software.
 * 
 * THE SOFTWARE IS PROVIDED “AS IS”, WITHOUT WARRANTY OF ANY KIND, EXPRESS OR 
 * IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY, FITNESS 
 * FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR
 * COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER 
 * IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION 
 * WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.
 * 
 * 
 * How to use this script
 * 
 * 1) Attach the script to one light source. It can be used with
 *    multiple light sources, only in ray casting mode (use_raycasting = true).
 *    
 * 2) Create 1 or more empty game objects (groups) named VPLS and add 
 *    point or spot light sources that represent Virtual Point Lights (VPLs).
 *    The name and the active state of these light sources is irrelevant. 
 *    Adjust their color and pose to match a representative position, 
 *    orientation and color of a reflective surface. Intensity is overriden.
 *    VLPS groups can be stationary or attached to any GameObject.
 *    
 * 3) Optionally, you can define one or more GameObjects named BLOCKERS, which 
 *    can contain (among other things) any number of light sources. These light
 *    sources represent spherical light suppression blobs. Only the position 
 *    and range parameters are relevant. All other light parameters are 
 *    disregarded. Blockers attenuate the contribution of a light source to 
 *    a VPL, according to  the distance of the line from the VPL to the source, 
 *    if the latter crosses the sphere of the blocker defined by the range 
 *    parameter.
 * 
 * 4) Spotlights can use the ray casting mode. With this, a temporary VPL 
 *    is generated at the intersection of the light's axis with the scene 
 *    (see next) and its reflectance attributes are interpolated from the 
 *    declared VPLs. It provides more accurate position for the bounce light 
 *    and can save the trouble of setting up blockers. On the other hand,
 *    it requires collision detection with the scene. Consider using few, 
 *    approximate colliders for better performance. 
 *    
 *    Script options
 *    
 *    use_raycasting: Enable or disable ray casting. Default is false.
 *    
 *    secondary_bounce: Enable approximate secondary bounce light. Default 
 *    is false.
 *    
 *    use_indirect_shadows: Enable shadow maps for VPLs. Default is false. 
 *    Warning, this can have a drastic impact on performance.
 *    
 *    automatic_weights: Compute the area-based weights of the VPLs that 
 *    correspond to their "importance" in the computation of the indirect lighting,
 *    automatically, amortized across N^2 frames, where N is the number of the VPLs.
 *    This means that VPL importance will be gradually updated to match the VPL spacing
 *    as the VPLs move within the scene. Default is false, in which case, all VPLs have 
 *    the same weight. 
 *    
 *    distance_scale: It is the divisor to adjust units to meters. It adjust the 
 *    reflected light brightness, due to distance attenuaton. If geometry is in 
 *    meters, set the  distance scale to 1 (default). If, for example, distances 
 *    are in feet, set distance scale to ~3. If units are in dm, set scale to 10 
 *    and so on.
 *
 *    avg_refl: Average albedo of the surfaces to use for the secondary bounce, 
 *    if enabled. Default value is 0.4.
 *
 *    avg_secondary_distance: is the distance to place the secondary bounce phantom
 *    VPL away from the cluster of contributing static VPLs
 *
 *    brdf_cookie: A light cookie to use for the VPLs to modulate their angular
 *    reflectance response. It is best to use one, for a smooth light gradient. 
 *    The script comes with a symmetrical cookie, whicj works well in most cases.
 *    
 *    For more details about the operation of the method, please see the paper.
 */

using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using System;

public class FakeGI : MonoBehaviour
{
    public bool use_raycasting = false;
    public bool secondary_bounce = false;
    public bool use_indirect_shadows = false;
    public bool automatic_weights = false;
    public float distance_scale = 1.0f;
    public float avg_refl = 0.4f; // average environment reflectance
    public float avg_secondary_distance = 1.0f; // distance to place 
	                                            // the phantom secondary bounce VPL
    public Texture brdf_cookie = null;

    protected List<Light> lights = new List<Light>();
    protected List<Color> reflectance = new List<Color>();
    protected List<float> weights = new List<float>();
    protected List<Light> blockers = new List<Light>();
    protected bool is_directional = false;
    protected bool is_spot = false;

    // smoothing parameters
    bool smooth = true;
    protected Vector3 old_vpl_pos;
    protected Vector3 old_vpl_normal;

    Light source;

    GameObject dynamic_vpl_go;
    Light dynamic_vpl;

    GameObject dynamic_vpl_go_secondary;
    Light dynamic_vpl_secondary;

    static int k = 0;
    static float d_min = 100000.0f;
    

    protected void UpdateWeightsAmortized()
    {
        if (lights.Count == 1)
        {
            weights[0] = 1.0f;
            return;
        }
        
        int current = k % lights.Count;
        int other = k / lights.Count;

        // completed one cycle, reset minimum distance;
        if (other == 0)
            d_min = 100000.0f;

        // same VPL, skip
        if (current == other)
        {
            k = (k + 1) % (lights.Count * lights.Count);
            return;
        }

        Vector3 v = lights[current].transform.position - lights[other].transform.position;
        float d = Vector3.Dot(v,v);
        if (d < d_min)
            d_min = d;

        // iterated over all other VPLs, time to update the weight
        if (other == lights.Count -1)
        {
            weights[current] = d_min;
        } 

        k = (k + 1) % (lights.Count * lights.Count);
    }


    protected void GetAllBlockers()
    {
        // search for all blocker groups in the scene, not just one.
        foreach (GameObject group in Resources.FindObjectsOfTypeAll(typeof(GameObject)) as GameObject[])
        {
            if (group.name != "BLOCKERS")
                continue;

            // fetch all children and keep only lights
            for (int i = 0; i < group.transform.childCount; i++)
            {
                Light l = group.transform.GetChild(i).gameObject.GetComponent<Light>();
                if (l != null)
                {
                    l.enabled = false;
                    blockers.Add(l);
                }
            }
        } // foreach object
    }

    protected void GetAllVPLs()
    {
        // search for all VPLS groups in the scene, not just one.
        foreach (GameObject group in Resources.FindObjectsOfTypeAll(typeof(GameObject)) as GameObject[])
        {
            if (group.name != "VPLS" || !group.activeInHierarchy)
                continue;
            
            // fetch all children and keep only lights
            for (int i = 0; i < group.transform.childCount; i++)
            {
                Light l = group.transform.GetChild(i).gameObject.GetComponent<Light>();
                
                if (l == null)
                    continue;

                
                // set up emission characteristics of VPLs
                if (l.type == LightType.Spot)
                {
                    l.innerSpotAngle = 0;
                    l.spotAngle = 170;
                    l.range = source.range;

                }
                
                // use the predefined intensity as area weighting factor
                weights.Add(l.intensity);

                // by default, disable all VPLs
                l.intensity = 0.0f;
                l.enabled = false;
                lights.Add(l);
                reflectance.Add(l.color);
                
                // set up indirect shadows (of any)
                if (use_indirect_shadows)
                {
                    l.shadows = LightShadows.Soft;
                    l.shadowCustomResolution = 32;
                }
                else
                    l.shadows = LightShadows.None;
            } // for VPLs
        } // foreach object
    }

    /** 
     *  Return the distance of a point q from a linear segment (x0,x1)
     */
    protected float PointToSegmentDistanceSquared(Vector3 q, Vector3 x0, Vector3 x1)
    {
        Vector3 dir = x1 - x0;
        float dist = 0.0f;
        Vector3 dir_norm = Vector3.Normalize(dir);
        float lq = Vector3.Dot(dir_norm, q - x0);
        if (lq < 0.0f)
        {
            Vector3 e = q - x0;
            dist = Vector3.Dot(e, e);
        }
        else if (lq > dir.magnitude)
        {
            Vector3 e = q - x1;
            dist = Vector3.Dot(e, e);
        }
        else
        {
            Vector3 o = dir_norm * lq + x0 - q;
            dist = Vector3.Dot(o, o);
        }
        return dist;
    }


    void Start()
    {
        source = this.GetComponent<Light>();
        is_directional = (source.type == LightType.Directional);
        is_spot = (source.type == LightType.Spot);

        // ray casting is only supported for spotlights.
        if (!is_spot && use_raycasting)
        {
            use_raycasting = false;
            Debug.Log("Warning: Ray tracing is enabled but is only supported for spotlights. Disabled.");
        }

        // Gather VPL references
        GetAllVPLs();

        // Gather blocker references
        GetAllBlockers();

        if (use_raycasting)
        {
            // Create a temporary phantom VPL to follow the light axis 
            dynamic_vpl_go = new GameObject();
            dynamic_vpl_go.AddComponent<Light>();
            dynamic_vpl = dynamic_vpl_go.GetComponent<Light>();
            dynamic_vpl.type = LightType.Spot;
            dynamic_vpl.innerSpotAngle = 0;
            dynamic_vpl.spotAngle = 155;
            dynamic_vpl.cookie = brdf_cookie;
            dynamic_vpl_go.name = "DynamicVPL";

            old_vpl_normal = -source.transform.forward;
            old_vpl_pos = new Vector3(0, 0, 0);

            // set up indirect shadows via a low-res shadow map
            if (use_indirect_shadows)
            {
                dynamic_vpl.shadows = LightShadows.Soft;
                dynamic_vpl.shadowCustomResolution = 64;
                dynamic_vpl.shadowBias = 0.03f;
                dynamic_vpl.shadowStrength = 0.5f;
            }
            else
                dynamic_vpl.shadows = LightShadows.None;
        }

        if (secondary_bounce)
        {
            // Create a second temporary phantom VPL to fill ambience from
            // secondary bounces. Depends on primary VPLs
            dynamic_vpl_go_secondary = new GameObject();
            dynamic_vpl_go_secondary.AddComponent<Light>();
            dynamic_vpl_secondary = dynamic_vpl_go_secondary.GetComponent<Light>();
            dynamic_vpl_secondary.type = LightType.Spot;
            dynamic_vpl_secondary.innerSpotAngle = 0;
            dynamic_vpl_secondary.spotAngle = 160;
            dynamic_vpl_secondary.cookie = brdf_cookie;
            dynamic_vpl_go_secondary.name = "DynamicVPL-secondary";
            dynamic_vpl_secondary.range = source.range;
        }
    }

    void Update()
    {
        if (automatic_weights)
            UpdateWeightsAmortized();

        Transform FL = source.transform;
        Vector3 dir = FL.forward;
        Vector3 pos = FL.position;
        float source_intensity = source.intensity;
        Color source_color = source.color;

        // Ray casting case
        if (use_raycasting)
        {
            // Record an intersection of the light axis with the scene
            RaycastHit hit;
            if (!Physics.Raycast(pos, dir, out hit))
            {
                dynamic_vpl.enabled = false;
                return;
            }

            // Adjust distance and attenuate 
            float dist = hit.distance/distance_scale;
            float intensity = source_intensity / (0.1f + dist*dist);

            // Adjust phantom VPL
            

            // if using phantom VPL position smoothing:
            if (old_vpl_pos.magnitude == 0.0)
                old_vpl_pos = hit.point;
            Vector3 dvpl_pos = smooth ? 0.5f*(hit.point + old_vpl_pos): hit.point;
            old_vpl_pos = dvpl_pos;

            dynamic_vpl.transform.position = dvpl_pos + dir * 0.6f;
            dynamic_vpl.color = new Color(0, 0, 0);

            // Interpolate reflectance and normal from user-defined VPLs
            float w_total = 0.0f;
            Vector3 vpl_normal = new Vector3(0.0f, 0.0f, 0.0f);
            float area_factor = 0.0f;

            for (int i = 0; i < lights.Count; i++)
            {
                Vector3 light_pos = lights[i].transform.position;
                Vector3 to_vpl = light_pos - dvpl_pos;
                float vpl_dist = to_vpl.magnitude / distance_scale;
                float w = 1.0f / (0.005f + vpl_dist* vpl_dist);
                dynamic_vpl.color += w * lights[i].color;
                area_factor += weights[i] * w;

                if (lights[i].type == LightType.Spot)
                {
                    vpl_normal += w * lights[i].transform.forward;
                }
                else
                {
                    vpl_normal += - w * dir;
                }
                
                w_total += w;
            }
            dynamic_vpl.color = dynamic_vpl.color * source_color / w_total;
            area_factor /= w_total;
            vpl_normal = Vector3.Normalize(vpl_normal);
            dynamic_vpl.transform.forward = smooth?0.5f*(old_vpl_normal+vpl_normal):vpl_normal;
            old_vpl_normal = vpl_normal;

            dynamic_vpl.enabled = true;
            float cos_theta_i = MathF.Max(Vector3.Dot(vpl_normal, -dir), 0.0f);
            intensity*= cos_theta_i * area_factor;
            dynamic_vpl.intensity = intensity;
            // Visualize phantom VPL:
            //Debug.DrawLine(dvpl_pos, dvpl_pos + 1.0f * vpl_normal, dynamic_vpl.color);

            if (secondary_bounce)
            {
                float prim_distance = hit.distance;
                dist = (0.5f*prim_distance + hit.distance)/distance_scale;
                float sec_int = avg_refl * intensity / (1.0f + dist*dist);
                dynamic_vpl_secondary.color = dynamic_vpl.color;
                dynamic_vpl_secondary.transform.forward= -vpl_normal;
                // set the phantom secondary bounce light behind the emitter
                Vector3 dvpl_pos_sec = dvpl_pos - hit.distance * 1.5f * dir;
                dynamic_vpl_secondary.transform.position = dvpl_pos_sec;
                dynamic_vpl_secondary.enabled = true;
                dynamic_vpl_secondary.intensity = sec_int;
       
            }
            return;
        } // using ray tracing

        // initialize secondary bounce light (if enabled)
        float sec_intensity = 0.0f;
        Vector3 sec_pos = new Vector3();
        Vector3 sec_dir = new Vector3();
        Color sec_color = new Color();
        float sec_weight = 0.0f;

        // If not using ray casting, iterate over all VPLs and adjust their contribution,
        // disabling insignificant ones.
        for (int i=0; i<lights.Count; i++ )
        {
            Vector3 light_pos = lights[i].transform.position;
            Vector3 to_vpl = is_directional ? source.transform.forward : light_pos - pos;
            Vector3 to_vpl_normalized = Vector3.Normalize(to_vpl);
            float dot = Vector3.Dot(to_vpl_normalized, dir);
            
            // Compute reflected light
            float intensity = source_intensity * weights[i];
            if (is_spot)
            {
                float angle_cos = MathF.Cos(3.14159f * this.GetComponent<Light>().spotAngle / 180.0f);
                intensity *= MathF.Max(0.0f, (dot - angle_cos) / (1.0f - angle_cos));
            }
            if (!is_directional)
            {
                float dist = to_vpl.magnitude / distance_scale;
                intensity *= 1.0f / (0.1f + dist * dist);
            }

            if (is_spot || is_directional)
            {
                Vector3 vpl_normal = lights[i].transform.forward;
                dot = MathF.Max(0.0f, Vector3.Dot(to_vpl_normalized, -vpl_normal));
                intensity *= dot;
            }
            

            // attenuate based on blockers (if any)
            if (blockers.Count > 0)
            {
                Vector3 endpoint = is_directional ? light_pos - 100.0f * to_vpl_normalized : pos;
                
                for (int j = 0; j < blockers.Count; j++)
                {
                    float dist_to_blocker = PointToSegmentDistanceSquared(blockers[j].transform.position, endpoint, light_pos);
                    float range = blockers[j].range;
                    float filter = MathF.Min(1.0f, dist_to_blocker / (0.0001f + range * range));
                    intensity *= filter;
                }
            }

            // cull insignificant VPLs
            if (intensity <= 0.01f)
            {
                lights[i].enabled = false;
            }
            else
            {
                lights[i].enabled = true;
                lights[i].intensity = intensity;
                lights[i].color = source_color * reflectance[i];
            }

            // update secondary bounce phantom light values (if enabled)
            if (secondary_bounce)
            {
                float w = intensity / source_intensity;
                sec_intensity += w * avg_refl * intensity;
                sec_color += w * lights[i].color;
                sec_pos += w * light_pos;
                sec_dir -= w * lights[i].transform.forward;
                sec_weight += w + 0.001f;
            }

        } // for all VPLs

        // update secondary bounce phantom light values (if enabled)
        if (secondary_bounce)
        {
            dynamic_vpl_secondary.transform.position = sec_pos / sec_weight - dir * avg_secondary_distance;
            dynamic_vpl_secondary.transform.forward = Vector3.Normalize(sec_dir);
            dynamic_vpl_secondary.intensity = sec_intensity / (sec_weight * avg_secondary_distance * avg_secondary_distance);
            dynamic_vpl_secondary.color = sec_color / sec_weight;


        }
    }

}
