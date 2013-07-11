﻿namespace Engine

#nowarn "9"

open System
open System.IO
open System.Runtime.InteropServices
open System.Threading
open System.Diagnostics
open Microsoft.FSharp.NativeInterop

[<Struct>]
[<StructLayout (LayoutKind.Sequential, CharSet = CharSet.Ansi)>]
type Vector3 =
    val X : float32
    val Y : float32
    val Z : float32
    
    new (x, y, z) = { X = x; Y = y; Z = z }
    new (vector: Vector3) = { X = vector.X; Y = vector.Y; Z = vector.Z }

    member this.Snap () =
        new Vector3 (float32 (int this.X), float32 (int this.Y), float32 (int this.Z))

