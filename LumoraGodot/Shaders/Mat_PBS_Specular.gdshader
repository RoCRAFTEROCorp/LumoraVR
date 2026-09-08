shader_type spatial;
// Specular-workflow PBS, back-face culling (the default).
//
// Ported from Unity's built-in Standard (Specular setup) shader, MIT licensed, Copyright (c) 2016
// Unity Technologies. The material hook swaps this for the front-cull or double-sided variant when
// Culling changes; uniform values live on the material and survive the swap.
render_mode blend_mix, depth_prepass_alpha, cull_back;

#include "res://Shaders/PBS_Specular_Core.gdshaderinc"
