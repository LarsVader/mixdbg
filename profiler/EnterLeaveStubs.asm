; EnterLeaveStubs.asm — x64 naked stubs for CLR FunctionEnter/Leave/Tailcall hooks
;
; The CLR calls these hooks with FunctionID in rcx but does NOT save volatile
; registers. We must preserve all volatile integer (rax, rcx, rdx, r8-r11) and
; XMM (xmm0-xmm5) registers, call the C++ implementation, then restore them.

EXTERN FunctionEnterImpl:PROC
EXTERN FunctionLeaveImpl:PROC
EXTERN FunctionTailcallImpl:PROC

.code

; Macro to save/restore volatile regs around a call to an impl function.
; The impl receives FunctionID in rcx (already there from the CLR).
SAVE_AND_CALL MACRO impl
    ; Save volatile integer registers (7 * 8 = 56 bytes)
    push rax
    push rcx
    push rdx
    push r8
    push r9
    push r10
    push r11
    ; Save volatile XMM registers (6 * 16 = 96 bytes) + 8 bytes alignment
    ; Total sub: 96 (xmm) + 8 (align) + 32 (shadow) = 136 = 88h
    sub rsp, 88h
    movdqa [rsp + 20h],      xmm0
    movdqa [rsp + 30h],      xmm1
    movdqa [rsp + 40h],      xmm2
    movdqa [rsp + 50h],      xmm3
    movdqa [rsp + 60h],      xmm4
    movdqa [rsp + 70h],      xmm5
    ; rcx still has FunctionID — shadow space at [rsp+0..rsp+1Fh]
    call impl
    ; Restore XMM
    movdqa xmm0, [rsp + 20h]
    movdqa xmm1, [rsp + 30h]
    movdqa xmm2, [rsp + 40h]
    movdqa xmm3, [rsp + 50h]
    movdqa xmm4, [rsp + 60h]
    movdqa xmm5, [rsp + 70h]
    add rsp, 88h
    ; Restore integer registers
    pop r11
    pop r10
    pop r9
    pop r8
    pop rdx
    pop rcx
    pop rax
    ret
ENDM

FunctionEnterNaked PROC
    SAVE_AND_CALL FunctionEnterImpl
FunctionEnterNaked ENDP

FunctionLeaveNaked PROC
    SAVE_AND_CALL FunctionLeaveImpl
FunctionLeaveNaked ENDP

FunctionTailcallNaked PROC
    SAVE_AND_CALL FunctionTailcallImpl
FunctionTailcallNaked ENDP

END
