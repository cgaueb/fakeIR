# Approximate Dynamic Global Illumination for VR

Unity script for implementing fake GI effects using fast instant radiosity with static VPLs, 
based on the article:
G. Papaioannou, Approximate Dynamic Global Illumination for VR, submitted to Springer Virtual Reality.

Author: Georgios Papaioannou
Copyright 2024 Georgios Papaioannou
MIT License
 
## How to use this script
1. Attach the script to one light source. It can be used with
   multiple light sources, only in ray casting mode (use_raycasting = true).

2. Create 1 or more empty game objects (groups) named VPLS and add 
   point or spot light sources that represent Virtual Point Lights (VPLs).
   The name and the active state of these light sources is irrelevant.
   Adjust their color and pose to match a representative position, 
   orientation and color of a reflective surface. Intensity is overriden.
   VLPS groups can be stationary or attached to any GameObject.
    
3. Optionally, you can define one or more GameObjects named BLOCKERS, which 
   can contain (among other things) any number of light sources. These light
   sources represent spherical light suppression blobs. Only the position 
   and range parameters are relevant. All other light parameters are 
   disregarded. Blockers attenuate the contribution of a light source to 
   a VPL, according to  the distance of the line from the VPL to the source, 
   if the latter crosses the sphere of the blocker defined by the range 
   parameter.

4. Spotlights can use the ray casting mode. With this, a temporary VPL 
   is generated at the intersection of the light's axis with the scene 
   (see next) and its reflectance attributes are interpolated from the 
   declared VPLs. It provides more accurate position for the bounce light 
   and can save the trouble of setting up blockers. On the other hand,
   it requires collision detection with the scene. Consider using few, 
   approximate colliders for better performance. 

## Script options
    
- **use_raycasting**: Enable or disable ray casting. Default is false.

- **secondary_bounce**: Enable approximate secondary bounce light. Default 
 is false.
     
 - **use_indirect_shadows**: Enable shadow maps for VPLs. Default is false. 
 Warning, this can have a drastic impact on performance.
     
 - **automatic_weights**: Compute the area-based weights of the VPLs that 
 correspond to their "importance" in the computation of the indirect lighting,
 automatically, amortized across N^2 frames, where N is the number of the VPLs.
 This means that VPL importance will be gradually updated to match the VPL spacing
 as the VPLs move within the scene. Default is false, in which case, all VPLs have 
 the same weight. 
    
 - **distance_scale**: It is the divisor to adjust units to meters. It adjust the 
 reflected light brightness, due to distance attenuaton. If geometry is in 
 meters, set the  distance scale to 1 (default). If, for example, distances 
 are in feet, set distance scale to ~3. If units are in dm, set scale to 10 
 and so on.
 
 - **avg_refl**: Average albedo of the surfaces to use for the secondary bounce, 
 if enabled. Default value is 0.4.
 
 - **avg_secondary_distance**: is the distance to place the secondary bounce phantom
 VPL away from the cluster of contributing static VPLs
 
 - **brdf_cookie**: A light cookie to use for the VPLs to modulate their angular
 reflectance response. It is best to use one, for a smooth light gradient. 
 The script comes with a symmetrical cookie, whicj works well in most cases.
    
 For more details about the operation of the method, please see the paper.
 
