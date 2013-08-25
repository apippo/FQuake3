﻿(*
Copyright (C) 2013 William F. Smith

This program is free software; you can redistribute it
and/or modify it under the terms of the GNU General Public License as
published by the Free Software Foundation; either version 2 of the License,
or (at your option) any later version.

This program is distributed in the hope that it will be
useful, but WITHOUT ANY WARRANTY; without even the implied warranty of
MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
GNU General Public License for more details.

You should have received a copy of the GNU General Public License
along with this program; if not, write to the Free Software
Foundation, Inc., 59 Temple Place, Suite 330, Boston, MA 02111-1307 USA

Derivative of Quake III Arena source:
Copyright (C) 1999-2005 Id Software, Inc.
*)

(*
    Rules of Thumb:
    1. Types that are 64 bytes or less can be structs.
    2. Types that don't singularly try to represent a value should be records.
*)

// Disable native interop warnings
#nowarn "9"
#nowarn "51"

namespace Engine.Renderer

open System
open System.IO
open System.Runtime.InteropServices
open System.Threading
open System.Diagnostics
open System.Diagnostics.Contracts
open Microsoft.FSharp.NativeInterop
open Engine.Core
open Engine.Math
open Engine.NativeInterop

/// <summary>
/// Based on Q3: CULL_IN, CULL_CLIP, CULL_OUT
/// ClipType
/// </summary>
type ClipType =
    | In = 0    // completely unclipped
    | Clip = 1  // clipped by one or more planes
    | Out = 2   // completely outside the clipping planes

/// <summary>
/// Based on Q3: PLANE_X, PLANE_Y, PLANE_Z, PLANE_NON_AXIAL
/// PlaneType
///
/// plane types are used to speed some tests
/// 0-2 are axial planes
/// </summary>
type PlaneType =
    | X = 0
    | Y = 1
    | Z = 2
    | NonAxial = 3

/// <summary>
/// Axis
/// </summary>
[<Struct>]
type Axis =
    val X : Vector3
    val Y : Vector3
    val Z : Vector3

    member inline this.Item
        with get (i) =
            match i with
            | 0 -> this.X
            | 1 -> this.Y
            | 2 -> this.Z
            | _ -> raise <| IndexOutOfRangeException ()

    new (x, y, z) =
        {
            X = x;
            Y = y;
            Z = z;
        }


/// <summary>
/// Rgba
/// </summary>
[<Struct>]
[<StructLayout (LayoutKind.Sequential)>]
type Rgba =
    val R : byte
    val G : byte
    val B : byte
    val A : byte

    member inline this.Item
        with get (i) =
            match i with
            | 0 -> this.R
            | 1 -> this.G
            | 2 -> this.B
            | 3 -> this.A
            | _ -> raise <| IndexOutOfRangeException ()

    new (r, g, b, a) =
        {
            R = r;
            G = g;
            B = b;
            A = a;
        }

/// <summary>
/// Based on Q3: orientation_t
/// Orientation
/// </summary>
[<Struct>]
type Orientation =
    val Origin : Vector3
    val Axis : Axis

    new (origin, axis) =
        {
            Origin = origin;
            Axis = axis;
        }

/// <summary>
/// Based on Q3: orientationr_t
/// OrientationR
///
/// Note: Should this be a record type? It is over 64 bytes, don't know for sure.
/// </summary>
[<Struct>]
type OrientationR =
    val Origin : Vector3        // in world coordinates
    val Axis : Axis             // orientation in world
    val ViewOrigin : Vector3    // viewParms->or.origin in local coordinates // FIXME: This directly points to viewParms orientation origin? Yuck.
    val ModelMatrix : Matrix16

    new (origin, axis, viewOrigin, modelMatrix) =
        {
            Origin = origin;
            Axis = axis;
            ViewOrigin = viewOrigin;
            ModelMatrix = modelMatrix;
        }

/// <summary>
/// Based on Q3: cplane_t
/// Plane
/// </summary>
[<Struct>]
[<StructLayout (LayoutKind.Explicit, Size = 20)>]
type Plane =
    [<FieldOffset (0)>]
    val Normal : Vector3

    [<FieldOffset (12)>]
    val Distance : single

    [<FieldOffset (16)>]
    [<MarshalAs (UnmanagedType.I8)>]
    val Type : PlaneType    // for fast side tests: 0,1,2 = axial, 3 = nonaxial

    [<FieldOffset (17)>]
    val SignBits : byte     // signx + (signy<<1) + (signz<<2), used as lookup during collision

(*
void SetPlaneSignbits (cplane_t *out) {
	int	bits, j;

	// for fast box on planeside test
	bits = 0;
	for (j=0 ; j<3 ; j++) {
		if (out->normal[j] < 0) {
			bits |= 1<<j;
		}
	}
	out->signbits = bits;
}
*)

    static member inline Zero = Plane (Vector3.zero, 0.f, PlaneType.X, 0uy)

    /// <summary>
    /// Based on Q3: SetPlaneSignBits
    /// CalculateSignBits
    /// </summary>
    static member inline CalculateSignBits (normal: Vector3) =
        let rec calculatePlaneSignBits bits acc =
            match acc with
            | 3 -> bits
            | _ ->
                calculatePlaneSignBits (match normal.[acc] < 0.f with | true -> bits ||| (1uy <<< acc) | _ -> bits) (acc + 1)

        calculatePlaneSignBits 0uy 0

    static member inline UpdateDistance distance (plane: Plane) =
        Plane (plane.Normal, distance, plane.Type, plane.SignBits);
(*
qboolean PlaneFromPoints( vec4_t plane, const vec3_t a, const vec3_t b, const vec3_t c ) {
	vec3_t	d1, d2;

	VectorSubtract( b, a, d1 );
	VectorSubtract( c, a, d2 );
	CrossProduct( d2, d1, plane );
	if ( VectorNormalize( plane ) == 0 ) {
		return qfalse;
	}

	plane[3] = DotProduct( a, plane );
	return qtrue;
}
*)

    /// <summary>
    /// Based on Q3: PlaneFromPoints
    /// InitFromPoints
    ///
    /// The normal will point out of the clock for clockwise ordered points
    /// </summary>
    static member inline InitFromPoints (a: Vector3) (b: Vector3) (c: Vector3) =
        let d1 = b - a
        let d2 = c - a
        let cross = Vector3.cross d2 d1
        let normal = Vector3.normalize cross
        
        match Vector3.length cross with
        | 0.f -> Plane (normal, 0.f, PlaneType.X, 0uy)
        | _ -> Plane (normal, Vector3.dot a normal, PlaneType.X, 0uy)

    new (normal, distance, typ, signBits) =
        {
            Normal = normal;
            Distance = distance;
            Type = typ;
            SignBits = signBits;
        }

    new (normal, distance, typ) =
        {
            Normal = normal;
            Distance = distance;
            Type = typ;
            SignBits = Plane.CalculateSignBits normal;
        }

/// <summary>
/// Bounds
/// </summary>
[<Struct>]
[<StructLayout (LayoutKind.Sequential)>]
type Bounds =
    val Bound0 : Vector3
    val Bound1 : Vector3     

    member inline this.Item
        with get (i) =
            match i with
            | 0 -> this.Bound0
            | 1 -> this.Bound1
            | _ -> raise <| IndexOutOfRangeException ()

    new (bound0, bound1) =
        {
            Bound0 = bound0;
            Bound1 = bound1;
        }

/// <summary>
/// Frustum
///
/// TODO: Find out if this is truly left, right, bottom, and top in this order
/// </summary>
type Frustum =
    {
        Left: Plane;
        Right: Plane;
        Bottom: Plane;
        Top: Plane;
    }

    member inline this.Item
        with get (i) =
            match i with
            | 0 -> this.Left
            | 1 -> this.Right
            | 2 -> this.Bottom
            | 3 -> this.Top
            | _ -> raise <| IndexOutOfRangeException ()

/// <summary>
/// Transform
///
/// Note: Too big to be a struct?
/// </summary>
[<Struct>]
type Transform =
    val T0 : Vector3
    val T1 : Vector3
    val T2 : Vector3
    val T3 : Vector3
    val T4 : Vector3
    val T5 : Vector3
    val T6 : Vector3
    val T7 : Vector3

    member inline this.Item
        with get (i) =
            match i with
            | 0 -> this.T0
            | 1 -> this.T1
            | 2 -> this.T2
            | 3 -> this.T3
            | 4 -> this.T4
            | 5 -> this.T5
            | 6 -> this.T6
            | 7 -> this.T7
            | _ -> raise <| IndexOutOfRangeException ()

    new (t0, t1, t2, t3, t4, t5, t6, t7) =
        {
            T0 = t0;
            T1 = t1;
            T2 = t2;
            T3 = t3;
            T4 = t4;
            T5 = t5;
            T6 = t6;
            T7 = t7;
        }

    static member inline Init (f: int -> Vector3) =
        Transform (f 0, f 1, f 2, f 3, f 4, f 5, f 6, f 7)


/// <summary>
/// Based on Q3: viewParms_t
/// ViewParms
/// </summary>
type ViewParms =
    {
        Orientation: OrientationR;
        World: OrientationR;
        PvsOrigin: Vector3;         // may be different than or.origin for portals
        IsPortal: bool;             // true if this view is through a portal
        IsMirror: bool;             // the portal is a mirror, invert the face culling
        FrameSceneId: int;          // copied from tr.frameSceneNum
        FrameCount: int;            // copied from tr.frameCount
        PortalPlane: Plane;         // clip anything behind this if mirroring
        ViewportX: int;
        ViewportY: int;
        ViewportWidth: int;
        ViewportHeight: int;
        FovX: single;
        FovY: single;
        ProjectionMatrix: Matrix16;
        Frustum: Frustum;
        VisibilityBounds: Bounds;
        ZFar: single;
    }

/// <summary>
/// Based on Q3: refEntityType_t
/// RefEntityType
/// </summary>
type RefEntityType =
    | Model = 0
    | Poly = 1
    | Sprite = 2
    | Beam = 3
    | RailCore = 4
    | RailRings = 5
    | Lightning = 6
    | PortalSurface = 7     // doesn't draw anything, just info for portals
    | MaxRefEntityType = 8

/// <summary>
/// Based on Q3: refEntity_t
/// RefEntity
/// </summary>
type RefEntity =
    {
        Type: RefEntityType;
        RenderFx: int;
        ModelHandle: int;                   // opaque type outside refresh
        LightingOrigin: Vector3;            // so multi-part models can be lit identically (RF_LIGHTING_ORIGIN)
        ShadowPlane: single;                // projection shadows go here, stencils go slightly lower
        Axis: Axis;                         // rotation vectors
        HasNonNormalizedAxes: bool;         // axis are not normalized, i.e. they have scale
        Origin: Vector3;                    // also used as MODEL_BEAM's "from"
        Frame: int;                         // also used as MODEL_BEAM's diameter
        OldOrigin: Vector3;                 // also used as MODEL_BEAM's "to"
        OldFrame: int;
        BackLerp: single;                   // 0.0 = current, 1.0 = old
        SkinId: int;                        // inline skin index
        CustomSkinHandle: int;              // NULL for default skin
        CustomShaderHandle: int;            // use one image for the entire thing
        ShaderRgba: Rgba;                   // colors used by rgbgen entity shaders
        ShaderTextureCoordinate: Vector2;   // texture coordinates used by tcMod entity modifiers
        ShaderTime: single;                 // subtracted from refdef time to control effect start times
        Radius: single;
        Rotation: single;
    }

/// <summary>
/// Based on Q3: trRefEntity_t
/// TrRefEntity
///
/// a trRefEntity_t has all the information passed in by
/// the client game, as well as some locally derived info
/// </summary>
type TrRefEntity =
    {
        Entity: RefEntity;
        AxisLength: single;         // compensate for non-normalized axis
        NeedDlights: bool;          // true for bmodels that touch a dlight
        IsLightingCalculated: bool;
        LightDirection: Vector3;    // normalized direction towards light
        AmbientLight: Vector3;      // color normalized to 0-255
        AmbientLightInt: int;       // 32 bit rgba packed
        DirectedLight: Vector3;
    }

/// <summary>
/// Based on Q3: dlight_t
/// Dlight
/// </summary>
[<Struct>]
type Dlight =
    val Origin : Vector3
    val Color : Vector3         // range from 0.0 to 1.0, should be color normalized
    val Radius : single
    val Transformed : Vector3   // origin in local coordinate system
    val Additive : int          // texture detail is lost tho when the lightmap is dark

    new (origin, color, radius, transformed, additive) =
        {
            Origin = origin;
            Color = color;
            Radius = radius;
            Transformed = transformed;
            Additive = additive;
        }

/// <summary>
/// Based on Q3: drawVert_t
/// DrawVertex
/// </summary>
[<Struct>]
type DrawVertex =
    val Vertex : Vector3
    val St1 : single
    val St2 : single
    val LightMap1 : single
    val LightMap2 : single
    val Normal : Vector3
    val Color : Rgba

    new (vertex, st1, st2, lightMap1, lightMap2, normal, color) =
        {
            Vertex = vertex;
            St1 = st1;
            St2 = st2;
            LightMap1 = lightMap1;
            LightMap2 = lightMap2;
            Normal = normal;
            Color = color;
        }

/// <summary>
/// Based on Q3: polyVert_t
/// PolyVertex
/// </summary>
[<Struct>]
type PolyVertex =
    val Vertex : Vector3
    val St1 : single;
    val St2 : single;
    val Modulate1 : byte;
    val Modulate2 : byte;
    val Modulate3 : byte;
    val Modulate4 : byte;

    new (vertex, st1, st2, modulate1, modulate2, modulate3, modulate4) =
        {
            Vertex = vertex;
            St1 = st1;
            St2 = st2;
            Modulate1 = modulate1;
            Modulate2 = modulate2;
            Modulate3 = modulate3;
            Modulate4 = modulate4;
        }

/// <summary>
/// Based on Q3: srfPoly_t
/// SurfacePoly
///
/// when cgame directly specifies a polygon, it becomes a srfPoly_t
/// as soon as it is called
/// </summary>
[<Struct>]
type SurfacePoly =
    val ShaderHandle : int
    val FogIndex : int
    val Vertices : PolyVertex list

    new (shaderHandle, fogIndex, vertices) =
        {
            ShaderHandle = shaderHandle;
            FogIndex = fogIndex;
            Vertices = vertices;
        }

/// <summary>
/// Based on Q3: srfDisplayList_t
/// SurfaceDisplayList
/// </summary>
[<Struct>]
type SurfaceDisplayList =
    val ListId : int

    new (listId) =
        {
            ListId = listId;
        }

/// <summary>
/// Based on Q3: srfFlare_t
/// SurfaceFlare
/// </summary>
[<Struct>]
type SurfaceFlare =
    val Origin : Vector3
    val Normal : Vector3
    val Color : Vector3

    new (origin, normal, color) =
        {
            Origin = origin;
            Normal = normal;
            Color = color;
        }

/// <summary>
/// Based on Q3: srfGridMesh_t
/// SurfaceGridMesh
/// </summary>
[<Struct>]
type SurfaceGridMesh =
    val DlightBit1 : int
    val DlightBit2 : int

    // culling information
    val MeshBounds : Bounds
    val LocalOrigin : Vector3
    val MeshRadius : single

    // lod information, which may be different
    // than the culling information to allow for
    // groups of curves that LOD as a unit
    val LodOrigin : Vector3
    val LodRadius : single
    val LodFixed : int
    val LodStitched : int

    val Width : int
    val Height : int
    val WidthLodError : single list
    val HeightLodError : single list
    val Vertex : DrawVertex         // variable sized

    new (dlightBit1, dlightBit2, meshBounds, localOrigin, meshRadius, lodOrigin, lodRadius, lodFixed, lodStitched, width, height, widthLodError, heightLodError, vertex) =
        {
            DlightBit1 = dlightBit1;
            DlightBit2 = dlightBit2;
            MeshBounds = meshBounds;
            LocalOrigin = localOrigin;
            MeshRadius = meshRadius;
            LodOrigin = lodOrigin;
            LodRadius = lodRadius;
            LodFixed = lodFixed;
            LodStitched = lodStitched;
            Width = width;
            Height = height;
            WidthLodError = widthLodError;
            HeightLodError = heightLodError;
            Vertex = vertex;
        }

/// <summary>
/// FaceVertexPoints
///
/// Note: Should this be a struct?
/// </summary>
[<Struct>]
type FaceVertexPoints =
    val Vertex0 : single
    val Vertex1 : single
    val Vertex2 : single
    val Vertex3 : single
    val Vertex4 : single
    val Vertex5 : single
    val Vertex6 : single
    val Vertex7 : single

    member inline this.Item
        with get (i) =
            match i with
            | 0 -> this.Vertex0
            | 1 -> this.Vertex1
            | 2 -> this.Vertex2
            | 3 -> this.Vertex3
            | 4 -> this.Vertex4
            | 5 -> this.Vertex5
            | 6 -> this.Vertex6
            | 7 -> this.Vertex7
            | _ -> raise <| IndexOutOfRangeException ()

    new (vertex0, vertex1, vertex2, vertex3, vertex4, vertex5, vertex6, vertex7) =
        {
            Vertex0 = vertex0;
            Vertex1 = vertex1;
            Vertex2 = vertex2;
            Vertex3 = vertex3;
            Vertex4 = vertex4;
            Vertex5 = vertex5;
            Vertex6 = vertex6;
            Vertex7 = vertex7;
        }

/// <summary>
/// Based on Q3: srfSurfaceFace_t
/// SurfaceFace
/// </summary>
[<Struct>]
type SurfaceFace =
    val Plane : Plane
    val DlightBit1 : int
    val DlightBit2 : int

    // triangle definitions (no normals at points)
    val PointCount : int
    val IndexCount : int
    val OfsIndices : int
    val Points : FaceVertexPoints   // variable sized; // there is a variable length list of indices here also

    new (plane, dlightBit1, dlightBit2, pointCount, indexCount, ofsIndices, points) =
        {
            Plane = plane;
            DlightBit1 = dlightBit1;
            DlightBit2 = dlightBit2;
            PointCount = pointCount;
            IndexCount = indexCount;
            OfsIndices = ofsIndices;
            Points = points;
        }

/// <summary>
/// Based on Q3: srfTriangles_t
/// SurfaceTriangles
///
/// // misc_models in maps are turned into direct geometry by q3map
/// </summary>
[<Struct>]
type SurfaceTriangles =
    val DlightBit1 : int
    val DlightBit2 : int

    // culling information (FIXME: use this!)
    val Bounds : Bounds
    val LocalOrigin : Vector3
    val Radius : single

    // triangle definitions
    val Indices : int list
    val Vertices : DrawVertex list

    new (dlightBit1, dlightBit2, bounds, localOrigin, radius, indices, vertices) =
        {
            DlightBit1 = dlightBit1;
            DlightBit2 = dlightBit2;
            Bounds = bounds;
            LocalOrigin = localOrigin;
            Radius = radius;
            Indices = indices;
            Vertices = vertices;
        }

/// <summary>
/// Based on Q3: surfaceType_t
/// Surface
///
/// any changes in surfaceType must be mirrored in rb_surfaceTable[] // FIXME: Oh crap..
/// </summary>
type Surface =
    | Bad
    | Skip                              // ignore
    | Face of SurfaceFace
    | Grid of SurfaceGridMesh
    | Triangles of SurfaceTriangles
    | Poly of SurfacePoly
    | Md3
    | Md4
    | Flare of SurfaceFlare
    | Entity                            // beams, rails, lightning, etc that can be determined by entity
    | DisplayList of SurfaceDisplayList

/// <summary>
/// Based on Q3: drawSurf_t
/// DrawSurface
/// </summary>
type DrawSurface =
    {
        Sort: uint32;       // bit combination for fast compares
        Surface : Surface
    }

/// <summary>
/// Based on Q3: RDF_NOWORLDMODEL, RDF_HYPERSPACE
/// RdFlags
/// </summary>
[<Flags>]
type RdFlags =
    | NoWorldModel = 0x1    // used for player configuration screen
    | Hyperspace = 0x4      // teleportation effect

/// <summary>
/// Based on Q3: trRefdef_t
/// TrRefDef
///
/// trRefdef_t holds everything that comes in refdef_t,
/// as well as the locally generated scene information
/// </summary>
type TrRefDef =
    {
        X: int;
        Y: int;
        Width: int;
        Height: int;
        ViewOrigin: Vector3;
        ViewAxis: Axis;             // transformation matrix
        Time: int;                  // time in milliseconds for shader effects and other time dependent rendering issues
        RdFlags: RdFlags;

        // 1 bits will prevent the associated area from rendering at all
        AreaMask: byte[]; // TODO: Remove array.
        HasAreaMaskModified: bool;  // qtrue if areamask changed since last scene

        FloatTime: single;          // tr.refdef.time / 1000.0
        Text: string seq;
        Entities: TrRefEntity seq;
        Dlights: Dlight seq;
        Polys:  SurfacePoly seq;
        DrawSurfaces: DrawSurface seq;
    }

    static member inline FindEntityById (entityId: int) (trRefDef: TrRefDef) =
        let length = Seq.length trRefDef.Entities

        // The entityId is based on the index of the sequence.
        match entityId >= length with
        | true -> raise <| IndexOutOfRangeException ()
        | _ ->  
        trRefDef.Entities
        |> Seq.nth entityId


/// <summary>
/// Based on Q3: image_t
/// Image
/// </summary>
type Image =
    {
        Path : string;          // game path, including extension
        Width : int;
        Height : int;

        // after power of two and picmip but not including clamp to MAX_TEXTURE_SIZE
        UploadWidth : int;
        UploadHeight : int

        TextureId : int;        // gl texture binding // TODO: Perhaps we should have GL specific types
        FrameUsed : int;
        InternalFormat : int;
        IsMipmap : bool;
        CanAllowPicmip : bool;
        WrapClampMode: int;     // GL_CLAMP or GL_REPEAT
        
        Next: Image option; // Is this a good idea?
    }

/// <summary>
/// Based on Q3: dshader_t
/// DShader
/// </summary>
type DShader =
    {
        Shader: string;
        SurfaceFlags: int;
        ContentFlags: int;
    }

/// <summary>
/// Based on Q3: cullType_t
/// CullType
/// </summary>
type CullType =
    | FrontSided = 0
    | BackSided = 1
    | TwoSided = 2

/// <summary>
/// Based on Q3: fogPass_t
/// FogType
/// </summary>
type FogType =
    | None = 0  // surface is translucent and will just be adjusted properly
    | Equal = 1 // surface is opaque but possibly alpha tested
    | Le = 2    // surface is trnaslucent, but still needs a fog pass (fog surface)

/// <summary>
/// Skybox
/// </summary>
type Skybox =
    {
        Image0: Image;
        Image1: Image;
        Image2: Image;
        Image3: Image;
        Image4: Image;
        Image5: Image;
    }

    member inline this.Item
        with get (i) =
            match i with
            | 0 -> this.Image0
            | 1 -> this.Image1
            | 2 -> this.Image2
            | 3 -> this.Image3
            | 4 -> this.Image4
            | 5 -> this.Image5
            | _ -> raise <| IndexOutOfRangeException ()

/// <summary>
/// Based on Q3: skyParms_t
/// SkyParms
/// </summary>
type SkyParms =
    {
        CloudHeight: single;
        Outerbox: Skybox option;
        Innerbox: Skybox option;
    }

/// <summary>
/// Based on Q3: fogParms_t
/// FogParms
/// </summary>
type FogParms =
    {
        Color: Vector3;
        DepthForOpaque: single;
    }

/// <summary>
/// Based on Q3: genFunc_t
/// WaveFormType
/// </summary>
type WaveFormType =
    | None = 0
    | Sin = 1
    | Square = 2
    | Triangle = 3
    | Sawtooth = 4
    | InverseSawtooth = 5
    | Noise = 6

/// <summary>
/// Based on Q3: waveForm_t
/// WaveForm
/// </summary>
type WaveForm =
    {
        Type: WaveFormType;
        Base: single;
        Amplitude: single;
        Phase: single;
        Frequency: single;
    }

/// <summary>
/// Based on Q3: deform_t
/// DeformType
/// </summary>
type DeformType =
    | None = 0
    | Wave = 1
    | Normals = 2
    | Bulge = 3
    | Move = 4
    | ProjectionShadow = 5
    | Autosprite = 6
    | Autosprite2 = 7
    | Text0 = 8
    | Text1 = 9
    | Text2 = 10
    | Text3 = 11
    | Text4 = 12
    | Text5 = 13
    | Text6 = 14
    | Text7 = 15

/// <summary>
/// Based on Q3: deformStage_t
/// DeformStage
/// </summary>
type DeformStage =
    {
        Type: DeformType;       // vertex coordinate modification type
        MoveVector: Vector3;
        Wave: WaveForm;
        Spread: single;
        BulgeWidth: single;
        BulgeHeight: single;
        BulgeSpeed: single;
    }

/// <summary>
/// Based on Q3: texCoordGen_t
/// TextureCoordinateType
/// </summary>
type TextureCoordinateType =
    | Bad = 0
    | Identity = 1          // clear to 0,0
    | Lightmap = 2
    | Texture = 3
    | EnvironmentMapped = 4
    | Fog = 5
    | Vector = 6            // S and T from world coordinates

/// <summary>
/// TextureCoordinateVectors
/// </summary>
[<Struct>]
type TextureCoordinateVectors =
    val X : Vector3
    val Y : Vector3

    member inline this.Item
        with get (i) =
            match i with
            | 0 -> this.X
            | 1 -> this.Y
            | _ -> raise <| IndexOutOfRangeException ()

    new (x, y) =
        {
            X = x;
            Y = y;
        }

/// <summary>
/// Based on Q3: texMod_t
/// TextureModification
/// </summary>
type TextureModificationType =
    | None = 0
    | Transform = 1
    | Turbulent = 2
    | Scroll = 3
    | Scale = 4
    | Stretch = 5
    | Rotate = 7
    | EntityTranslate = 7

/// <summary>
/// Based on Q3: texModInfo_t
/// TextureModification
/// </summary>
type TextureModification =
    {
        Type: TextureModificationType;

        // used for TMOD_TURBULENT and TMOD_STRETCH
        Wave: WaveForm;

        // used for TMOD_TRANSFORM
        Matrix: Matrix4;        // s' = s * m[0][0] + t * m[1][0] + trans[0]
        Translate: Vector2;     // t' = s * m[0][1] + t * m[0][1] + trans[1]

        // used for TMOD_SCALE
        Scale: Vector2;         // s *= scale[0]
                                // t *= scale[1]

        // used for TMOD_SCROLL
        Scroll: Vector2;        // s' = s + scroll[0] * time
                                // t' = t + scroll[1] * time

        // + = clockwise
        // - = counterclockwise
        RotateSpeed: single;
    }

/// <summary>
/// Based on Q3: textureBundle_t
/// TextureBundle
/// </summary>
type TextureBundle =
    {
        ImageAnimations: Image seq;
        ImageAnimationSpeed: single;
        TextureCoordinateType: TextureCoordinateType;
        TextureCoordinateVectors: TextureCoordinateVectors;
        TextureModifications: TextureModification seq;
        VideMapHandle: int;
        IsLightmap: bool;
        IsVertexLightmap: bool;
        IsVideoMap: bool;
    }

/// <summary>
/// Based on Q3: colorGen_t
/// ShaderColorType
/// </summary>
type ShaderColorType =
    | Bad = 0
    | IdentityLighting = 1  // tr.identityLight
    | Identity = 2          // always (1,1,1,1)
    | Entity = 3            // grabbed from entity's modulate field
    | OneMinusEntity = 4    // grabbed from 1 - entity.modulate
    | ExactVertex = 5       // tess.vertexColors
    | Vertex = 6            // tess.vertexColors * tr.identityLight
    | OneMinuxVertex = 7
    | Waveform = 8          // programmatically generated
    | LightingDiffuse = 9
    | Fog = 10              // standard fog
    | Const = 11            // fixed color

/// <summary>
/// Based on Q3: alphaGen_t
/// ShaderAlphaType
/// </summary>
type ShaderAlphaType =
    | Identity = 0
    | Skip = 1
    | Entity = 2
    | OneMinusEntity = 3
    | Vertex = 4
    | OneMinusVertex = 5
    | LightingSpecular = 6
    | Waveform = 7
    | Portal = 8
    | Const = 9

/// <summary>
/// Based on Q3: acff_t (acff stands for adjustColorForFog .. lol)
/// FogColorType
/// </summary>
type FogColorType =
    | None = 0
    | Rgb = 1
    | Rgba = 2
    | Alpha = 3

/// <summary>
/// Based on Q3: shaderStage_t
/// ShaderStage
/// </summary>
type ShaderStage =
    {
        Active: bool;
        TextureBundle1: TextureBundle;
        TextureBundle2: TextureBundle;
        RgbWave: WaveForm;
        RgbColorType: ShaderColorType;
        AlphaWave: WaveForm;
        AlphaType: ShaderAlphaType;
        ConstantColor: Rgba;            // for CGEN_CONST and AGEN_CONST
        StateBits: int;                 // GLS_xxxx mask
        FogColorType: FogColorType;
        IsDetail: bool;
    }

/// <summary>
/// Based on Q3: shader_t
/// Shader
/// </summary>
type Shader =
    {
        Name: string;               // game path, including extension
        LightmapIndex: int;         // for a shader to match, both name and lightmapIndex must match
        Index: int;                 // this shader == tr.shaders[index]
        SortedIndex: int;           // this shader == tr.sortedShaders[sortedIndex]
        Sort: single;               // lower numbered shaders draw before higher numbered

        // we want to return index 0 if the shader failed to
        // load for some reason, but R_FindShader should
        // still keep a name allocated for it, so if
        // something calls RE_RegisterShader again with
        // the same name, we don't try looking for it again
        IsDefaultShader: bool;

        IsExplicitlyDefined: bool;  // found in a .shader file
        SurfaceFlags: int;          // if explicitlyDefined, this will have SURF_* flags
        ContentFlags: int;
        IsEntityMergable: bool;     // merge across entites optimizable (smoke, blood)
        IsSky: bool;
        Sky: SkyParms;
        Fog: FogParms;
        PortalRange: single;        // distance to fog out at
        MultitextureEnv: int;       // 0, GL_MODULATE, GL_ADD (FIXME: put in stage)
        CullType: CullType;         // CT_FRONT_SIDED, CT_BACK_SIDED, or CT_TWO_SIDED
        HasPolygonOffset: bool;     // set for decals and other items that must be offset 
        HasNoMipMaps: bool;         // for console fonts, 2D elements, etc.
        HasNoPicMip: bool;          // for images that must always be full resolution
        FogType: FogType;           // draw a blended pass, possibly with depth test equals
        NeedsNormal: bool;          // not all shaders will need all data to be gathered
        NeedsSt1: bool;
        NeedsSt2: bool;
        NeedsColor: bool;
        Deforms: DeformStage seq;
        Stages: ShaderStage seq;
        // void (*optimimalStageIteratorFunc)( void ); <-- TODO: Need to figure what to do with this guy.
        ClampTime: single;              // time this shader is clamped to
        TimeOffset: single;             // current time offset for this shader

        // Is StateId a better name vs. numStates?
        StateId: int;                   // if non-zero this is a state shader
        CurrentShader: Shader option;   // current state if this is a state shader
        ParentShader: Shader option;    // current state if this is a state shader
        CurrentState: int;              // current state index for cycle purposes
        ExpireTime: int64;              // time in milliseconds this expires
        RemappedShader: Shader option;  // current shader this one is remapped too
        ShaderStates: int seq;          // index to valid shader states
        Next: Shader option;
    }

/// <summary>
/// Based on Q3: msurface_t
/// DShader
/// </summary>
type MSurface =
    {
        ViewCount: int;         // if == tr.viewCount, already added
        Shader: Shader option;
        FogIndex: int;
        Data: Surface option;   // any of srf*_t
    }

/// <summary>
/// Based on Q3: bmodel_t
/// BModel
/// </summary>
type BModel =
    {
        Bounds: Bounds;         // for culling
        Surfaces: MSurface seq;
    }

/// <summary>
/// Based on Q3: mnode_t
/// MNode
/// </summary>
type MNode =
    {
        // common with leaf and node
        Contents: int;          // -1 for nodes, to differentiate from leafs
        VisFrame: int;          // node needs to be traversed if current

        // for bounding box culling
        Mins: Vector3;
        Maxs: Vector3;
        Parent: MNode option;

        // node specific
        Plane: Plane option;
        Child1: MNode option;
        Child2: MNode option;

        // leaf specific
        Cluster: int;
        Area: int;

        MarkSurfaces: MSurface seq;
    }

/// <summary>
/// Based on Q3: fog_t
/// Fog
/// </summary>
type Fog =
    {
        OriginalBrushId: int;
        Bounds: Bounds;
        Color: Rgba;                    // in packed byte format
        TextureCoordinateScale: single; // texture coordinate vector scales
        Parms: FogParms;

        // for clipping distance in fog when outside
        HasSurface: bool;
        Surface: Vector4;
    }

[<Struct>]
type LightGridBounds =
    val Bound0 : int
    val Bound1 : int
    val Bound2 : int

    member inline this.Item
        with get (i) =
            match i with
            | 0 -> this.Bound0
            | 1 -> this.Bound1
            | 2 -> this.Bound2
            | _ -> raise <| IndexOutOfRangeException ()

    new (bound0, bound1, bound2) =
        {
            Bound0 = bound0;
            Bound1 = bound1;
            Bound2 = bound2;
        }

/// <summary>
/// Based on Q3: world_t
/// World
/// </summary>
type World =
    {
        Name: string;           // ie: maps/tim_dm2.bsp
        BaseName: string;       // ie: tim_dm2
        DataSize: int;
        Shaders: DShader seq;
        BModels: BModel seq;
        Planes: Plane seq;
        Nodes: MNode seq;
        Surfaces: MSurface seq;
        MarkSurfaces: MSurface seq;
        Fogs: Fog seq;
        LightGridOrigin: Vector3;
        LightGridSize: Vector3;
        LightGridInverseSize: Vector3;
        LightGridBounds: LightGridBounds;
        LightGridData: byte option; // FIXME: this right? byte *lightGridData
        ClusterCount: int;
        ClusterByteCount: int;

        // FIXME: I dont think this is right, looks like it may be just data. We'll see.
        Vis: byte option;           // may be passed in by CM_LoadMap to save space
        NoVis: byte option;         // clusterBytes of 0xff

        EntityString: string;
        EntityParsePoint: string;
    }

/// <summary>
/// Based on Q3: md3Header_t
/// Md3Header
/// </summary>
[<Struct>]
type Md3Header =
    val Id : int
    val Version : int
    val Name : string       // model name
    val Flags : int
    val FrameCount : int
    val TagCount : int
    val SurfaceCount : int
    val SkinCount : int
    val FrameOffset : int   // first surface
    val TagOffset : int     // numFrames * numTags
    val SurfaceOffset : int // first surface, others follow
    val EndOffset : int     // end of file

/// <summary>
/// Based on Q3: md4Header_t
/// M43Header
/// </summary>
[<Struct>]
type Md4Header =
    val Id : int
    val Version : int
    val Name : string           // model name

    // frames and bones are shared by all levels of detail
    val FrameCount : int
    val BoneCount : int
    val BoneNameOffset : int    // char name[ MAX_QPATH ]   
    val FrameOffset : int       // md4Frame_t[numFrames]

    // each level of detail has completely separate sets of surfaces
    val LodCount : int
    val LodOffset : int
    val EndOffset : int         // end of file

/// <summary>
/// Based on Q3: modtype_t
/// ModelType
/// </summary>
type ModelType =
    | Bad = 0
    | Brush = 1
    | Mesh = 2
    | Md4 = 3

/// <summary>
/// Based on Q3: model_t
/// Model
/// </summary>
type Model =
    {
        Name: string;
        Type: ModelType;
        Index: int;
        DataSize: int;
        BModel: BModel option;
        Md3: Md3Header;

    }

/// <summary>
/// Based on Q3: trGlobals_t
/// TrGlobals
///
/// Most renderer globals are defined here.
/// backend functions should never modify any of these fields,
/// but may read fields that aren't dynamically modified
/// by the frontend.
/// TODO: Not finished.
/// </summary>
type TrGlobals =
    {
        IsRegistered : bool;            // cleared at shutdown, set at beginRegistration
        VisCount : int;                 // incremented every time a new vis cluster is entered
        FrameCount : int;               // incremented every frame
        SceneCount : int;               // incremented every scene
        ViewCount : int;                // incremented every view (twice a scene if portaled) and every R_MarkFragments call
        SmpFrame : int;                 // toggles from 0 to 1 every endFrame
        FrameSceneId : int;             // zeroed at RE_BeginFrame
        HasWorldMapLoaded : bool;
        World: World;

        // FIXME: This is data. We need a immutable array type structure. A list may be ok, not sure.
        ExternalVisData: byte option;   // from RE_SetWorldVisData, shared with CM_Load
        
        DefaultImage: Image option;
        ScratchImages: Image seq;
        FogImage: Image option;
        DlightImage: Image option;              // inverse-quare highlight for projective adding
        FlareImage: Image option;
        WhiteImage: Image option;               // full of 0xff
        IentityLightImage: Image option;        // full of tr.identityLightByte

        DefaultShader: Shader option;
        ShadowShader: Shader option;
        ProjectionShadowShader: Shader option;
        FlareShader: Shader option;
        SunShader: Shader option;

        Lightmaps: Image seq;

        CurrentEntity: TrRefEntity option;
        WorldEntity: TrRefEntity;               // point currentEntity at this when rendering world
        CurrentEntityId: int;
        ShiftedEntityId: int;                   // currentEntityNum << QSORT_ENTITYNUM_SHIFT
        CurrentModel: Model option;
        // TODO:


        // May not be in order.
        RefDef: TrRefDef;
        ViewParms: ViewParms;
        Orientation: OrientationR;
    }

    static member inline UpdateCurrentEntityById entityId (tr: TrGlobals) =
        let entity = TrRefDef.FindEntityById entityId tr.RefDef
        { tr with CurrentEntity = Some entity; CurrentEntityId = entityId }

(*
=======================================================================================================================
Interop Types
=======================================================================================================================
*)

[<Struct>]
[<StructLayout (LayoutKind.Sequential)>]
type cplane_t =
    val mutable normal : vec3_t
    val mutable dist : single
    val mutable type' : byte
    val mutable signbits : byte
    val mutable pad : byte
    val private pad1 : byte

[<Struct>]
[<StructLayout (LayoutKind.Sequential)>]
type orientation_t =
    val mutable origin : vec3_t
    val mutable axis : vec3_t
    val private axis1 : vec3_t
    val private axis2 : vec3_t

[<Struct>]
[<StructLayout (LayoutKind.Sequential)>]
type orientationr_t =
    val mutable origin : vec3_t
    val mutable axis : vec3_t
    val private axis1 : vec3_t
    val private axis2 : vec3_t
    val mutable viewOrigin : vec3_t
    val mutable modelMatrix : single
    val private modelMatrix1 : single
    val private modelMatrix2 : single
    val private modelMatrix3 : single
    val private modelMatrix4 : single
    val private modelMatrix5 : single
    val private modelMatrix6 : single
    val private modelMatrix7 : single
    val private modelMatrix8 : single
    val private modelMatrix9 : single
    val private modelMatrix10 : single
    val private modelMatrix11 : single
    val private modelMatrix12 : single
    val private modelMatrix13 : single
    val private modelMatrix14 : single
    val private modelMatrix15 : single

[<Struct>]
[<StructLayout (LayoutKind.Sequential)>]
type viewParms_t =
    val mutable or' : orientationr_t
    val mutable world : orientationr_t
    val mutable pvsOrigin : vec3_t
    val mutable isPortal : qboolean
    val mutable isMirror : qboolean
    val mutable frameSceneNum : int
    val mutable frameCount : int
    val mutable portalPlane : cplane_t
    val mutable viewportX : int
    val mutable viewportY : int
    val mutable viewportWidth : int
    val mutable viewportHeight : int
    val mutable fovX : single
    val mutable fovY : single
    val mutable projectionMatrix : single
    val private projectionMatrix1 : single
    val private projectionMatrix2 : single
    val private projectionMatrix3 : single
    val private projectionMatrix4 : single
    val private projectionMatrix5 : single
    val private projectionMatrix6 : single
    val private projectionMatrix7 : single
    val private projectionMatrix8 : single
    val private projectionMatrix9 : single
    val private projectionMatrix10 : single
    val private projectionMatrix11 : single
    val private projectionMatrix12 : single
    val private projectionMatrix13 : single
    val private projectionMatrix14 : single
    val private projectionMatrix15 : single
    val mutable frustum : cplane_t
    val private frustum1 : cplane_t
    val private frustum2 : cplane_t
    val private frustum3 : cplane_t
    val mutable visBounds : vec3_t
    val private visBounds1 : vec3_t
    val mutable zFar : single
    
type surfaceType_t =
    | SF_BAD = 0
    | SF_SKIP = 1
    | SF_FACE = 2
    | SF_GRID = 3
    | SF_TRIANGLES = 4
    | SF_POLY = 5
    | SF_MD3 = 6
    | SF_MD4 = 7
    | SF_FLARE = 8
    | SF_ENTITY = 9
    | SF_DISPLAY_LIST = 10
    | SF_NUM_SURFACE_TYPES = 11
    | SF_MAX = 0x7fffffff

[<Struct>]
[<StructLayout (LayoutKind.Sequential)>]
type drawVert_t =
    val mutable xyz : vec3_t
    val mutable st : single
    val private st1 : single
    val mutable lightmap : single
    val mutable lightmap1 : single
    val mutable normal : vec3_t
    val mutable color : byte
    val private color1 : byte
    val private color2 : byte
    val private color3 : byte

[<Struct>]
[<StructLayout (LayoutKind.Sequential)>]
type polyVert_t =
    val mutable xyz : vec3_t
    val mutable st : single
    val private st1 : single
    val mutable modulate : byte
    val private modulate1 : byte
    val private modulate2 : byte
    val private modulate3 : byte

[<Struct>]
[<StructLayout (LayoutKind.Sequential)>]
type poly_t =
    val mutable hShader : qhandle_t
    val mutable numVerts : int
    val mutable verts : nativeptr<polyVert_t>

[<Struct>]
[<StructLayout (LayoutKind.Sequential)>]
type srfPoly_t =
    val mutable surfaceType : surfaceType_t
    val mutable hShader : qhandle_t
    val mutable fogIndex : int
    val mutable numVerts : int
    val mutable verts : nativeptr<polyVert_t>

[<Struct>]
[<StructLayout (LayoutKind.Sequential)>]
type srfDisplayList_t =
    val mutable surfaceType : surfaceType_t
    val mutable listNum : int

[<Struct>]
[<StructLayout (LayoutKind.Sequential)>]
type srfGridMesh_t =
    val mutable surfaceType : surfaceType_t
    val mutable dlightBits : int
    val private dlightBits1 : int
    val mutable meshBounds : vec3_t
    val private meshBounds1 : vec3_t
    val mutable localOrigin : vec3_t
    val mutable meshRadius : single
    val mutable lodOrigin : vec3_t
    val mutable lodRadius : single
    val mutable lodFixed : int
    val mutable lodStitched : int
    val mutable width : int
    val mutable height : int
    val mutable widthLodError : nativeptr<single>
    val mutable heightLodError : nativeptr<single>
    val mutable verts : drawVert_t

[<Struct>]
[<StructLayout (LayoutKind.Sequential)>]
type srfFlare_t =
    val mutable surfaceType : surfaceType_t
    val mutable origin : vec3_t
    val mutable normal : vec3_t
    val mutable color : vec3_t

[<Struct>]
[<StructLayout (LayoutKind.Sequential)>]
type srfSurfaceFace_t =
    val mutable surfaceType : surfaceType_t
    val mutable plane : cplane_t
    val mutable dlightBits : int
    val private dlightBits1 : int
    val mutable numPoints : int
    val mutable numIndices : int
    val mutable ofsIndices : int
    val mutable points : single
    val private points1 : single
    val private points2 : single
    val private points3 : single
    val private points4 : single
    val private points5 : single
    val private points6 : single
    val private points7 : single
        
[<Struct>]
[<StructLayout (LayoutKind.Sequential)>]
type srfTriangles_t =
    val mutable surfaceType : surfaceType_t
    val mutable dlightBits : int
    val private dlightBits1 : int
    val mutable bounds : vec3_t
    val private bounds1 : vec3_t
    val mutable localOrigin : vec3_t
    val mutable radius : single
    val mutable numIndexes : int
    val mutable indexes : nativeptr<int>
    val mutable numVerts : int
    val mutable verts : nativeptr<drawVert_t>

[<Struct>]
[<StructLayout (LayoutKind.Sequential)>]
type drawSurf_t =
    val mutable sort : uint32
    val mutable surface : nativeptr<surfaceType_t>

type refEntityType_t =
    | RT_MODEL = 0
    | RT_POLY = 1
    | RT_SPRITE = 2
    | RT_BEAM = 3
    | RT_RAIL_CORE = 4
    | RT_RAIL_RINGS = 5
    | RT_LIGHTNING = 6
    | RT_PORTALSURFACE = 7
    | RT_MAX_REF_ENTITY_TYPE = 8

[<Struct>]
[<StructLayout (LayoutKind.Sequential)>]
type refEntity_t =
    val mutable reType : refEntityType_t
    val mutable renderfx : int
    val mutable hModel : qhandle_t
    val mutable lightingOrigin : vec3_t
    val mutable shadowPlane : single
    val mutable axis : vec3_t
    val private axis1 : vec3_t
    val private axis2 : vec3_t
    val mutable nonNormalizedAxes : qboolean
    val mutable origin : single
    val private origin1 : single
    val private origin2 : single
    val mutable frame : int
    val mutable oldorigin : single
    val private oldorigin1 : single
    val private oldorigin2 : single
    val mutable oldframe : int
    val mutable backlerp : single
    val mutable skinNum : int
    val mutable customSkin : qhandle_t
    val mutable customShader : qhandle_t
    val mutable shaderRGBA : byte
    val private shaderRGBA1 : byte
    val private shaderRGBA2 : byte
    val private shaderRGBA3 : byte
    val mutable shaderTexCoord : single
    val private shaderTexCoord1 : single
    val mutable shaderTime : single
    val mutable radius : single
    val mutable rotation : single

[<Struct>]
[<StructLayout (LayoutKind.Sequential)>]
type trRefEntity_t =
    val mutable e : refEntity_t
    val mutable axisLength : single
    val mutable needDlights : qboolean
    val mutable lightingCalculated : qboolean
    val mutable lightDir : vec3_t
    val mutable ambientLight : vec3_t
    val mutable ambientLightInt : int
    val mutable directedLight : vec3_t

[<Struct>]
[<StructLayout (LayoutKind.Sequential)>]
type world_t =
    val mutable name : char
    // TODO:

[<Struct>]
[<StructLayout (LayoutKind.Sequential)>]
type image_t =
    val mutable imgName : char
    // TODO:

[<Struct>]
[<StructLayout (LayoutKind.Sequential)>]
type shader_t =
    val mutable shaderName : char
    // TODO:


[<Struct>]
[<StructLayout (LayoutKind.Sequential)>]
type trGlobals_t =
    val mutable registered : qboolean
    val mutable visCount : int
    val mutable frameCount : int
    val mutable sceneCount : int
    val mutable viewCount : int
    val mutable smpFrame : int
    val mutable frameSceneNum : int
    val mutable worldMapLoaded : qboolean
    val mutable world : nativeptr<world_t>
    val mutable externalVisData : nativeptr<byte>
    val mutable defaultImage : nativeptr<image_t>
    val mutable scratchImage : nativeptr<image_t>
    val private scratchImage1 : nativeptr<image_t>
    val private scratchImage2 : nativeptr<image_t>
    val private scratchImage3 : nativeptr<image_t>
    val private scratchImage4 : nativeptr<image_t>
    val private scratchImage5 : nativeptr<image_t>
    val private scratchImage6 : nativeptr<image_t>
    val private scratchImage7 : nativeptr<image_t>
    val private scratchImage8 : nativeptr<image_t>
    val private scratchImage9 : nativeptr<image_t>
    val private scratchImage10 : nativeptr<image_t>
    val private scratchImage11 : nativeptr<image_t>
    val private scratchImage12 : nativeptr<image_t>
    val private scratchImage13 : nativeptr<image_t>
    val private scratchImage14 : nativeptr<image_t>
    val private scratchImage15 : nativeptr<image_t>
    val private scratchImage16 : nativeptr<image_t>
    val private scratchImage17 : nativeptr<image_t>
    val private scratchImage18 : nativeptr<image_t>
    val private scratchImage19 : nativeptr<image_t>
    val private scratchImage20 : nativeptr<image_t>
    val private scratchImage21 : nativeptr<image_t>
    val private scratchImage22 : nativeptr<image_t>
    val private scratchImage23 : nativeptr<image_t>
    val private scratchImage24 : nativeptr<image_t>
    val private scratchImage25 : nativeptr<image_t>
    val private scratchImage26 : nativeptr<image_t>
    val private scratchImage27 : nativeptr<image_t>
    val private scratchImage28 : nativeptr<image_t>
    val private scratchImage29 : nativeptr<image_t>
    val private scratchImage30 : nativeptr<image_t>
    val private scratchImage31 : nativeptr<image_t>
    val mutable fogImage : nativeptr<image_t>
    val mutable dlightImage : nativeptr<image_t>
    val mutable flareImage : nativeptr<image_t>
    val mutable whiteImage : nativeptr<image_t>
    val mutable identityLightImage : nativeptr<image_t>
    // TODO:

type Orientation with
    static member inline ofNative (native: orientation_t) =
        Orientation (
            NativePtr.toStructure &&native.origin,
            NativePtr.toStructure &&native.axis
        )

type OrientationR with
    static member inline ofNative (native: orientationr_t) =
        OrientationR (
            NativePtr.toStructure &&native.origin,
            NativePtr.toStructure &&native.axis,
            NativePtr.toStructure &&native.viewOrigin,
            NativePtr.toStructure &&native.modelMatrix
        )

type Plane with
    static member inline ofNative (native: cplane_t) =
        Plane (
            NativePtr.toStructure &&native.normal,
            native.dist,
            NativePtr.toStructure &&native.type',
            native.signbits
        )

    static member inline toNative (plane: Plane) =
        let mutable native = cplane_t ()
        NativePtr.ofStructure plane.Normal &&native.normal
        native.dist <- plane.Distance
        NativePtr.ofStructure plane.Type &&native.type'
        native.signbits <- plane.SignBits
        native

type Frustum with
    static member inline ofNativePtr (nativePtr: nativeptr<cplane_t>) =
        {
            Left = Plane.ofNative <| NativePtr.get nativePtr 0;
            Right = Plane.ofNative <| NativePtr.get nativePtr 1;
            Bottom = Plane.ofNative <| NativePtr.get nativePtr 2;
            Top = Plane.ofNative <| NativePtr.get nativePtr 3;
        }

    static member inline toNativePtr (frustum: Frustum) (nativePtr: nativeptr<cplane_t>) =
        NativePtr.set nativePtr 0 <| Plane.toNative frustum.Left
        NativePtr.set nativePtr 1 <| Plane.toNative frustum.Right
        NativePtr.set nativePtr 2 <| Plane.toNative frustum.Bottom
        NativePtr.set nativePtr 3 <| Plane.toNative frustum.Top
        nativePtr

type ViewParms with
    static member inline ofNative (native: viewParms_t) =
        {
            Orientation = OrientationR.ofNative native.or';
            World = OrientationR.ofNative native.world;
            PvsOrigin = NativePtr.toStructure &&native.pvsOrigin;
            IsPortal = Convert.ToBoolean native.isPortal;
            IsMirror = Convert.ToBoolean native.isMirror;
            FrameSceneId = native.frameSceneNum;
            FrameCount = native.frameCount;
            PortalPlane = Plane.ofNative native.portalPlane;
            ViewportX = native.viewportX;
            ViewportY = native.viewportY;
            ViewportWidth = native.viewportWidth;
            ViewportHeight = native.viewportHeight;
            FovX = native.fovX;
            FovY = native.fovY;
            ProjectionMatrix = NativePtr.toStructure &&native.projectionMatrix;
            Frustum = Frustum.ofNativePtr &&native.frustum;
            VisibilityBounds = NativePtr.toStructure &&native.visBounds;
            ZFar = native.zFar;
        }

type DrawVertex with
    static member inline ofNative (native: drawVert_t) =
        DrawVertex (
            NativePtr.toStructure &&native.xyz,
            NativePtr.get &&native.st 0,
            NativePtr.get &&native.st 1,
            NativePtr.get &&native.lightmap 0,
            NativePtr.get &&native.lightmap 1,
            NativePtr.toStructure &&native.normal,
            NativePtr.toStructure &&native.color
        )

type PolyVertex with
    static member inline ofNative (native: polyVert_t) =
        PolyVertex (
            NativePtr.toStructure &&native.xyz,
            NativePtr.get &&native.st 0,
            NativePtr.get &&native.st 1,
            NativePtr.get &&native.modulate 0,
            NativePtr.get &&native.modulate 1,
            NativePtr.get &&native.modulate 2,
            NativePtr.get &&native.modulate 3
        )

type Surface with
    static member inline ofNativeFace (native: srfSurfaceFace_t) =
        SurfaceFace (
            Plane.ofNative native.plane,
            NativePtr.get &&native.dlightBits 0,
            NativePtr.get &&native.dlightBits 1,
            native.numPoints,
            native.numIndices,
            native.ofsIndices,
            NativePtr.toStructure &&native.points
        )
        |> Face

    static member inline ofNativeTriangles (native: srfTriangles_t) =
        SurfaceTriangles (
            NativePtr.get &&native.dlightBits 0,
            NativePtr.get &&native.dlightBits 1,
            NativePtr.toStructure &&native.bounds,
            NativePtr.toStructure &&native.localOrigin,
            native.radius,
            List.ofNativePtrArray native.numIndexes native.indexes,
            List.ofNativePtrObjArray native.numVerts native.verts
        )
        |> Triangles

    static member inline ofNativePoly (native: srfPoly_t) =
        SurfacePoly (
            native.hShader,
            native.fogIndex,
            List.ofNativePtrObjArray native.numVerts native.verts
        )
        |> Poly

    static member inline ofNativeDisplayList (native: srfDisplayList_t) =
        SurfaceDisplayList (
            native.listNum
        )
        |> DisplayList

    static member inline ofNativeGridMesh (native: srfGridMesh_t) =
        SurfaceGridMesh (
            NativePtr.get &&native.dlightBits 0,
            NativePtr.get &&native.dlightBits 1,
            NativePtr.toStructure &&native.meshBounds,
            NativePtr.toStructure &&native.localOrigin,
            native.meshRadius,
            NativePtr.toStructure &&native.lodOrigin,
            native.lodRadius,
            native.lodFixed,
            native.lodStitched,
            native.width,
            native.height,
            List.ofNativePtrArray native.width native.widthLodError,
            List.ofNativePtrArray native.height native.heightLodError,
            NativePtr.toStructure &&native.verts
        )
        |> Grid

    static member inline ofNativeFlare (native: srfFlare_t) =
        SurfaceFlare (
            NativePtr.toStructure &&native.origin,
            NativePtr.toStructure &&native.normal,
            NativePtr.toStructure &&native.color
        )
        |> Flare

    static member inline ofNativePtr (nativePtr: nativeptr<surfaceType_t>) =
        let type' = NativePtr.read nativePtr
        match type' with
        | surfaceType_t.SF_BAD -> Bad
        | surfaceType_t.SF_SKIP -> Skip
        | surfaceType_t.SF_FACE ->
            let nativePtr = NativePtr.ofNativeInt<srfSurfaceFace_t> <| NativePtr.toNativeInt nativePtr
            let native = NativePtr.read nativePtr
            Surface.ofNativeFace native            
        | surfaceType_t.SF_GRID ->
            let nativePtr = NativePtr.ofNativeInt<srfGridMesh_t> <| NativePtr.toNativeInt nativePtr
            let native = NativePtr.read nativePtr
            Surface.ofNativeGridMesh native
        | surfaceType_t.SF_TRIANGLES ->
            let nativePtr = NativePtr.ofNativeInt<srfTriangles_t> <| NativePtr.toNativeInt nativePtr
            let native = NativePtr.read nativePtr
            Surface.ofNativeTriangles native
        | surfaceType_t.SF_POLY ->
            let nativePtr = NativePtr.ofNativeInt<srfPoly_t> <| NativePtr.toNativeInt nativePtr
            let native = NativePtr.read nativePtr
            Surface.ofNativePoly native
        | surfaceType_t.SF_MD3 -> Md3
        | surfaceType_t.SF_MD4 -> Md4
        | surfaceType_t.SF_FLARE ->
            let nativePtr = NativePtr.ofNativeInt<srfFlare_t> <| NativePtr.toNativeInt nativePtr
            let native = NativePtr.read nativePtr
            Surface.ofNativeFlare native 
        | surfaceType_t.SF_ENTITY -> Entity
        | surfaceType_t.SF_DISPLAY_LIST ->
            let nativePtr = NativePtr.ofNativeInt<srfDisplayList_t> <| NativePtr.toNativeInt nativePtr
            let native = NativePtr.read nativePtr
            Surface.ofNativeDisplayList native
        | _ -> raise <| Exception "Invalid Surface Type"          

type DrawSurface with
    static member inline ofNative (native: drawSurf_t) =
        {
            Sort = native.sort;
            Surface = Surface.ofNativePtr native.surface;
        }

type RefEntity with
    static member inline ofNative (native: refEntity_t) =
        {
            RefEntity.Type = enum<RefEntityType> (int native.reType);
            RenderFx = native.renderfx;
            ModelHandle = native.hModel;
            LightingOrigin = NativePtr.toStructure &&native.lightingOrigin;
            ShadowPlane = native.shadowPlane;
            Axis = NativePtr.toStructure &&native.axis;
            HasNonNormalizedAxes = Convert.ToBoolean native.nonNormalizedAxes;
            Origin = NativePtr.toStructure &&native.origin;
            Frame = native.frame;
            OldOrigin = NativePtr.toStructure &&native.oldorigin;
            OldFrame = native.oldframe;
            BackLerp = native.backlerp;
            SkinId = native.skinNum;
            CustomSkinHandle = native.customSkin;
            CustomShaderHandle = native.customShader;
            ShaderRgba = NativePtr.toStructure &&native.shaderRGBA;
            ShaderTextureCoordinate = NativePtr.toStructure &&native.shaderTexCoord;
            ShaderTime = native.shaderTime;
            Radius = native.radius;
            Rotation = native.rotation;
        }

type TrRefEntity with
    static member inline ofNative (native: trRefEntity_t) =
        {
            Entity = RefEntity.ofNative native.e;
            AxisLength = native.axisLength;
            NeedDlights = Convert.ToBoolean native.needDlights;
            IsLightingCalculated = Convert.ToBoolean native.lightingCalculated;
            LightDirection = NativePtr.toStructure &&native.lightDir;
            AmbientLight = NativePtr.toStructure &&native.ambientLight;
            AmbientLightInt = native.ambientLightInt;
            DirectedLight = NativePtr.toStructure &&native.directedLight;
        }
        