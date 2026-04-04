; EnterLeaveStubs.asm — x64 naked stubs for CLR FunctionEnter/Leave/Tailcall hooks
;
; The CLR calls these hooks with FunctionID in rcx but does NOT save volatile
; registers. We must preserve all volatile integer (rax, rcx, rdx, r8-r11) and
; XMM (xmm0-xmm5) registers, call the C++ implementation, then restore them.
;
; Stack alignment: at function entry, RSP ≡ 8 mod 16 (return addr pushed).
; After 7 pushes (56 bytes), RSP ≡ 0 mod 16. Sub 128 keeps it aligned.
; RSP ≡ 0 mod 16 before "call" — correct per x64 ABI.

EXTERN FunctionEnterImpl:PROC
EXTERN FunctionLeaveImpl:PROC
EXTERN FunctionTailcallImpl:PROC

.code

SAVE_AND_CALL MACRO impl
    ; Save volatile integer registers (7 pushes = 56 bytes)
    push rax
    push rcx
    push rdx
    push r8
    push r9
    push r10
    push r11
    ; RSP now ≡ 0 mod 16. Allocate 128 bytes (≡ 0 mod 16):
    ;   [rsp+00..1F] = shadow space (32 bytes)
    ;   [rsp+20..7F] = xmm0-5 save area (96 bytes)
    sub rsp, 80h
    movdqa [rsp+20h], xmm0
    movdqa [rsp+30h], xmm1
    movdqa [rsp+40h], xmm2
    movdqa [rsp+50h], xmm3
    movdqa [rsp+60h], xmm4
    movdqa [rsp+70h], xmm5
    ; rcx still has FunctionID. RSP ≡ 0 mod 16.
    call impl
    ; Restore XMM
    movdqa xmm0, [rsp+20h]
    movdqa xmm1, [rsp+30h]
    movdqa xmm2, [rsp+40h]
    movdqa xmm3, [rsp+50h]
    movdqa xmm4, [rsp+60h]
    movdqa xmm5, [rsp+70h]
    add rsp, 80h
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
