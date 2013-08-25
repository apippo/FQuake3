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

// Disable native interop warnings
#nowarn "9"
#nowarn "51"

namespace Engine.Command

open System
open System.IO
open System.Text
open System.Runtime.InteropServices
open System.Threading
open System.Diagnostics
open Microsoft.FSharp.NativeInterop
open Engine.NativeInterop

module private Native =
    type XCommand = delegate of unit -> unit

    [<DllImport (LibQuake3, CallingConvention = DefaultCallingConvention)>]
    extern void Cmd_AddCommand (string cmdName, XCommand func)

    [<DllImport (LibQuake3, CallingConvention = DefaultCallingConvention)>]
    extern int Cmd_Argc ()

    [<DllImport (LibQuake3, CallingConvention = DefaultCallingConvention)>]
    extern void Cmd_ArgsBuffer (StringBuilder buffer, int length)

module Command =
    let Add (name: string) (f: unit -> unit) =
        let cmd = Native.XCommand (f)
        GCHandle.Alloc (cmd, GCHandleType.Pinned) |> ignore
        Native.Cmd_AddCommand (name, cmd)

    let Argc () =
        Native.Cmd_Argc ()
