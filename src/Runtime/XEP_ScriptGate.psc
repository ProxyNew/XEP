Scriptname XEP_ScriptGate extends Quest
{Owned Chance None controls follow source completion events, initialization and
 load. The three FormLists are aligned. Each pass is finite; completion is idle.
 Requires SKSE for mod events and leveled-item reads.}

FormList Property Receivers Auto
FormList Property Payloads Auto
FormList Property Gates Auto
String[] Property CompletionEvents Auto
Int Property ChecksPerUpdate = 16 Auto

Int cursor = -1
Bool followUp = False
Bool updateRunning = False

Event OnInit()
    PlayerLoaded()
EndEvent

Function PlayerLoaded()
    Int i = 0
    While i < CompletionEvents.Length
        RegisterForModEvent(CompletionEvents[i], "OnSourceCompleted")
        i += 1
    EndWhile
    RequestPass()
EndFunction

Event OnSourceCompleted(String eventName, String strArg, Float numArg, Form sender)
    RequestPass()
EndEvent

Function RequestPass()
    If cursor >= 0
        followUp = True
    EndIf
    ; Always register: an event arriving as the last batch finishes must still
    ; wake a pass. Single updates on this script coalesce into one pending update.
    RegisterForSingleUpdate(0.1)
EndFunction

Event OnUpdate()
    If updateRunning
        Return
    EndIf
    updateRunning = True
    If cursor < 0
        cursor = 0
        followUp = False
    EndIf
    Int total = Gates.GetSize()
    Int stop = cursor + ChecksPerUpdate
    While cursor < total && cursor < stop
        GlobalVariable gate = Gates.GetAt(cursor) as GlobalVariable
        If gate
            Float value = 100.0
            If Contains(Receivers.GetAt(cursor) as LeveledItem, Payloads.GetAt(cursor))
                value = 0.0
            EndIf
            If gate.GetValue() != value
                gate.SetValue(value)
            EndIf
        EndIf
        cursor += 1
    EndWhile
    If cursor < total
        RegisterForSingleUpdate(0.1)
    ElseIf followUp
        cursor = 0
        followUp = False
        RegisterForSingleUpdate(0.1)
    Else
        cursor = -1
    EndIf
    updateRunning = False
EndEvent

; Script additions are appended, so search from the end.
Bool Function Contains(LeveledItem list, Form item)
    If !list || !item
        Return False
    EndIf
    Int i = list.GetNumForms()
    While i > 0
        i -= 1
        If list.GetNthForm(i) == item
            Return True
        EndIf
    EndWhile
    Return False
EndFunction
