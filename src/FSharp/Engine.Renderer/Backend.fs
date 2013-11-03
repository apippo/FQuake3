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

module Engine.Renderer.Backend

// Disable native interop warnings
#nowarn "9"
#nowarn "51"

open System
open System.Diagnostics.Contracts
open System.Runtime.InteropServices
open Microsoft.FSharp.NativeInterop
open Engine.Core
open Engine.Math
open Engine.Renderer.Core
open GL

// Hmm, I wonder if this is ok...
let inline fixed' (f: nativeptr<_> -> unit) (a: obj) =
    let handle = GCHandle.Alloc (a, GCHandleType.Pinned)
    let addr = handle.AddrOfPinnedObject ()

    f <| NativePtr.ofNativeInt addr

    handle.Free ()

[<RequireQualifiedAccess>]
module GL =
    [<RequireQualifiedAccess>]
    module GLS =
        [<Literal>]
        let SrcBlendZero = 0x00000001

        [<Literal>]
        let SrcBlendOne = 0x00000002

        [<Literal>]
        let SrcBlendDstColor = 0x00000003

        [<Literal>]
        let SrcBlendOneMinusDstColor = 0x00000004

        [<Literal>]
        let SrcBlendSrcAlpha = 0x00000005

        [<Literal>]
        let SrcBlendOneMinusSrcAlpha = 0x00000006

        [<Literal>]
        let SrcBlendDstAlpha = 0x00000007

        [<Literal>]
        let SrcBlendOneMinusDstAlpha = 0x00000008

        [<Literal>]
        let SrcBlendAlphaSaturate = 0x00000009

        [<Literal>]
        let SrcBlendBits = 0x0000000f

        [<Literal>]
        let DstBlendZero = 0x00000010

        [<Literal>]
        let DstBlendOne = 0x00000020

        [<Literal>]
        let DstBlendSrcColor = 0x00000030

        [<Literal>]
        let DstBlendOneMinusSrcColor = 0x00000040

        [<Literal>]
        let DstBlendSrcAlpha = 0x00000050

        [<Literal>]
        let DstBlendOneMinusSrcAlpha = 0x00000060

        [<Literal>]
        let DstBlendDstAlpha = 0x00000070

        [<Literal>]
        let DstBlendOneMinusDstAlpha = 0x00000080

        [<Literal>]
        let DstBlendBits = 0x000000f0

        [<Literal>]
        let DepthMaskTrue = 0x00000100

        [<Literal>]
        let PolyModeLine = 0x00001000

        [<Literal>]
        let DepthTestDisable = 0x00010000

        [<Literal>]
        let DepthFuncEqual = 0x00020000

        [<Literal>]
        let ATestGt0 = 0x10000000

        [<Literal>]
        let ATestLt80 = 0x20000000

        [<Literal>]
        let ATestGe80 = 0x40000000

        [<Literal>]
        let ATestBits = 0x70000000

        [<Literal>]
        let Default = DepthMaskTrue

    /// Based on Q3: GL_State
    /// state
    ///
    /// This routine is responsible for setting the most commonly changed state
    /// in Q3.
    let state (stateBits: uint64) (state: GLState) =
        let diff = stateBits ^^^ state.GLStateBits
        
        match diff with
        | 0UL -> state
        | _ ->

        //
        // check depthFunc bits
        //
        if diff &&& uint64 GLS.DepthFuncEqual <> 0UL then
            match stateBits &&& uint64 GLS.DepthFuncEqual <> 0UL with
            | true ->
                glDepthFunc <| GLenum GL_EQUAL
            | _ ->
                glDepthFunc <| GLenum GL_LEQUAL

        //
        // check blend bits
        //
        if diff &&& uint64 (GLS.SrcBlendBits ||| GLS.DstBlendBits) <> 0UL then
            if stateBits &&& uint64 (GLS.SrcBlendBits ||| GLS.DstBlendBits) <> 0UL then
                let srcFactor =
                    match int (stateBits &&& uint64 GLS.SrcBlendBits) with
                    | GLS.SrcBlendZero -> GL_ZERO
                    | GLS.SrcBlendOne -> GL_ONE
                    | GLS.SrcBlendDstColor -> GL_DST_COLOR
                    | GLS.SrcBlendOneMinusDstColor -> GL_ONE_MINUS_DST_COLOR
                    | GLS.SrcBlendSrcAlpha -> GL_SRC_ALPHA
                    | GLS.SrcBlendOneMinusSrcAlpha -> GL_ONE_MINUS_SRC_ALPHA
                    | GLS.SrcBlendDstAlpha -> GL_DST_ALPHA
                    | GLS.SrcBlendOneMinusDstAlpha -> GL_ONE_MINUS_DST_ALPHA
                    | GLS.SrcBlendAlphaSaturate -> GL_SRC_ALPHA_SATURATE
                    | _ -> raise <| Exception "Invalid src blend state bits."
                
                let dstFactor =
                    match int (stateBits &&& uint64 GLS.DstBlendBits) with
                    | GLS.DstBlendZero -> GL_ZERO
                    | GLS.DstBlendOne -> GL_ONE
                    | GLS.DstBlendSrcColor -> GL_SRC_COLOR
                    | GLS.DstBlendOneMinusSrcColor -> GL_ONE_MINUS_SRC_COLOR
                    | GLS.DstBlendSrcAlpha -> GL_SRC_ALPHA
                    | GLS.DstBlendOneMinusSrcAlpha -> GL_ONE_MINUS_SRC_ALPHA
                    | GLS.DstBlendDstAlpha -> GL_DST_ALPHA
                    | GLS.DstBlendOneMinusDstAlpha -> GL_ONE_MINUS_DST_ALPHA
                    | _ -> raise <| Exception "Invalid dst blend state bits."

                glEnable <| GLenum GL_BLEND
                glBlendFunc (GLenum srcFactor, GLenum dstFactor)
            else
                glDisable <| GLenum GL_BLEND

        //
        // check depthmask
        //
        if diff &&& uint64 GLS.DepthMaskTrue <> 0UL then
            if stateBits &&& uint64 GLS.DepthMaskTrue <> 0UL then
                glDepthMask <| GLboolean GL_TRUE
            else
                glDepthMask <| GLboolean GL_FALSE
        
        //
        // fill/line mode
        //
        if diff &&& uint64 GLS.PolyModeLine <> 0UL then
            if stateBits &&& uint64 GLS.PolyModeLine <> 0UL then
                glPolygonMode (GLenum GL_FRONT_AND_BACK, GLenum GL_LINE)
            else
                glPolygonMode (GLenum GL_FRONT_AND_BACK, GLenum GL_FILL)

        //
        // depthtest
        //
        if diff &&& uint64 GLS.DepthTestDisable <> 0UL then
            if stateBits &&& uint64 GLS.DepthTestDisable <> 0UL then
                glDisable <| GLenum GL_DEPTH_TEST
            else
                glEnable <| GLenum GL_DEPTH_TEST

        //
        // alpha test
        //
        if diff &&& uint64 GLS.ATestBits <> 0UL then
            match int (stateBits &&& uint64 GLS.ATestBits) with
            | 0 ->
                glDisable <| GLenum GL_ALPHA_TEST
            | GLS.ATestGt0 ->
                glEnable <| GLenum GL_ALPHA_TEST
                glAlphaFunc (GLenum GL_GREATER, 0.f)
            | GLS.ATestLt80 ->
                glEnable <| GLenum GL_ALPHA_TEST
                glAlphaFunc (GLenum GL_LESS, 0.5f)
            | GLS.ATestGe80 ->
                glEnable <| GLenum GL_ALPHA_TEST
                glAlphaFunc (GLenum GL_GEQUAL, 0.5f)
            | _ ->
                raise <| Exception "Invalid alpha test state bits."
        
        { state with GLStateBits = stateBits }


/// Based on Q3: SetViewportAndScissor
/// SetViewportAndScissor
let setViewportAndScissor (backend: Backend) =
    let view = backend.View

    glMatrixMode <| GLenum GL_PROJECTION
    fixed' (fun ptr -> glLoadMatrixf ptr) view.ProjectionMatrix
    glMatrixMode <| GLenum GL_MODELVIEW

    // set the window clipping
    glViewport (view.ViewportX, view.ViewportY, view.ViewportWidth, view.ViewportHeight)
    glScissor (view.ViewportX, view.ViewportY, view.ViewportWidth, view.ViewportHeight)