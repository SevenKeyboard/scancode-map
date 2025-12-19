#Requires AutoHotkey v1.1.36+
#Include %A_ScriptDir%
#Include .\lib\Event.ahk
#Include .\lib\WaitForSingleObjectAsync.ahk
;==============================================================
; ScancodeMap — Read/write Windows "Scancode Map" and watch for changes
;
; GitHub: https://github.com/SevenKeyboard/scancode-map
; Author: SevenKeyboard Ltd. (2025)
; License: MIT License
;==============================================================

/*
Example Usage:
    #SingleInstance Force
    #Persistent
    ScancodeMap.onChanged(func("onScancodeMapChanged"))
    onScancodeMapChanged()    {
        tooltip % ScancodeMap.read()
    }
*/

class VersionManager_ScancodeMap
{
    static _ := VersionManager_ScancodeMap._init()
    _init()    {
        global
        SCANCODEMAP_VERSION := "1.0.0"
        if (!this._verCheck(EVENT_VERSION, "1.0.0"))
            throw exception("Event version 1.x is required (minimum 1.0.0).")
        if (!this._verCheck(WAITFORSINGLEOBJECTASYNC_VERSION, "1.0.0"))
            throw exception("WaitForSingleObjectAsync version 1.x is required (minimum 1.0.0).")
        return true
    }
    _verCheck(byRef actual, required)    {
        if !isSet(actual)
            return false
        actualMajor     := strSplit(actual, ".",, 2)[1]
        requiredMajor   := strSplit(required, ".",, 2)[1]
        if (actualMajor !== requiredMajor)
            return false
        return verCompare(actual, ">=" required)
    }
}
class ScancodeMap
{
    static _keyName:="HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Control\Keyboard Layout"
    read()    {
        regRead value, % this._keyName, % "Scancode Map"
        return (!errorLevel?value:"")
    }
    exist(ByRef value:="")    {
        regRead value, % this._keyName, % "Scancode Map"
        return (!errorLevel)
    }
    write(value)    {
        regWrite % "REG_BINARY", % this._keyName, % "Scancode Map", % value
        return (!errorLevel)
    }
    delete()    {
        regDelete % this._keyName, % "Scancode Map"
        return (!errorLevel)
    }
    ;--------------------------------
    getList(mapData := "")    {
        result:=[], i:=1
        loop Parse, % (mapData !== "" ? mapData : this.read())
        {
            result[i] .= A_LoopField
            if (!mod(A_Index, 8))
                ++i
        }
        return result
    }
    ;--------------------------------
    static _handles := {event:"", waitFunc:"", hKey:""}
        ,_objbmCloseRegistryKeysOnExit  := objBindMethod(ScancodeMap, "_closeRegistryKeysOnExit")
        ,_objbmChangedCallback          := objBindMethod(ScancodeMap, "_changedCallback")
        ,_userCallbacks := []
        
    onChanged(callback, addRemove := 1)    { ;  Handle callbacks as succinctly as possible.
        static hasRegCallback := false
            ,HKEY_LOCAL_MACHINE         := 0x80000002
            ,KEY_NOTIFY                 := 0x0010
            ,ERROR_SUCCESS              := 0
            ,REG_NOTIFY_CHANGE_LAST_SET := 0x00000004
        critical % format("{2}", prevIC := A_IsCritical, "On")
        if (!hasRegCallback)    {
            onExit(this._objbmCloseRegistryKeysOnExit)
            loop 1    {
                this._handles.event := new Event()
                if (!this._handles.event.handle)    {
                    this._handles.event := ""
                    break
                }
                this._handles.waitFunc := new WaitForSingleObjectAsync(this._handles.event.handle, this._objbmChangedCallback)
                lErrorCode := dllCall("Advapi32.dll\RegOpenKeyEx", "Ptr",HKEY_LOCAL_MACHINE, "Str","SYSTEM\CurrentControlSet\Control\Keyboard Layout", "UInt",0, "UInt",KEY_NOTIFY, "Ptr*",hKey, "Int")
                if (lErrorCode !== ERROR_SUCCESS)    {
                    this._handles.waitFunc  := ""
                    this._handles.event     := ""
                    break
                }
                this._handles.hKey := hKey
                dllCall("Advapi32.dll\RegNotifyChangeKeyValue", "Ptr",this._handles.hKey, "Int",false, "UInt",REG_NOTIFY_CHANGE_LAST_SET, "Ptr",this._handles.event.handle, "Int",true, "Int")
                hasRegCallback := true
            }
            if (!hasRegCallback)    {
                critical % prevIC
                return
            }
        }
        newList := []
        switch (addRemove)
        {
            case 1:
                for _, fn in this._userCallbacks    {
                    if (fn !== callback)
                        newList.push(fn)
                }
                newList.push(callback)
            case -1:
                newList.push(callback)
                for _, fn in this._userCallbacks    {
                    if (fn !== callback)
                        newList.push(fn)
                }
            case 0:
                for _, fn in this._userCallbacks    {
                    if (fn !== callback)
                        newList.push(fn)
                }
        }
        this._userCallbacks := newList
        if (!this._userCallbacks.length())    {
            if (this._handles.hKey)
                dllCall("Advapi32.dll\RegCloseKey", "Ptr",this._handles.hKey, "Int")
            this._handles.waitFunc  := ""
            this._handles.event     := ""
            hasRegCallback := false
        }
        critical % prevIC
    }
    _changedCallback()    {
        static REG_NOTIFY_CHANGE_LAST_SET := 0x00000004
        if (this._userCallbacks.length())    {
            for _, fn in this._userCallbacks
                if (fn.call())
                    break
        }
        dllCall("Advapi32.dll\RegNotifyChangeKeyValue", "Ptr",this._handles.hKey, "Int",false, "UInt",REG_NOTIFY_CHANGE_LAST_SET, "Ptr",this._handles.event.handle, "Int",true, "Int")
    }
    _closeRegistryKeysOnExit(exitReason, exitCode)    {
        if (this._handles.hKey)
            dllCall("Advapi32.dll\RegCloseKey", "Ptr",this._handles.hKey, "Int")
        this._handles.waitFunc  := ""
        this._handles.event     := ""
    }
}