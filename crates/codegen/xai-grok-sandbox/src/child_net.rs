// Modified by the AgentDesk project for Windows desktop integration and safety support.
//! Per-child seccomp network filter. No-op on non-Linux.

#[cfg(target_os = "linux")]
fn blocked_network_syscalls() -> &'static [i64] {
    &[
        libc::SYS_socket,
        libc::SYS_socketpair,
        libc::SYS_connect,
        libc::SYS_bind,
        libc::SYS_sendto,
        libc::SYS_sendmsg,
        libc::SYS_sendmmsg,
        libc::SYS_recvfrom,
        libc::SYS_recvmsg,
        libc::SYS_recvmmsg,
        libc::SYS_listen,
        libc::SYS_accept,
        libc::SYS_accept4,
        libc::SYS_shutdown,
        libc::SYS_io_uring_setup,
        libc::SYS_io_uring_enter,
        libc::SYS_io_uring_register,
    ]
}

#[cfg(target_os = "linux")]
fn native_audit_arch() -> Option<u32> {
    #[cfg(target_arch = "x86_64")]
    {
        return Some(0xC000_003E);
    }
    #[cfg(target_arch = "aarch64")]
    {
        return Some(0xC000_00B7);
    }
    #[cfg(target_arch = "x86")]
    {
        return Some(0x4000_0003);
    }
    #[cfg(target_arch = "arm")]
    {
        return Some(0x4000_0028);
    }
    #[allow(unreachable_code)]
    None
}

/// Install seccomp BPF filter blocking network syscalls.
///
/// # Safety
///
/// Must be called in a `pre_exec` context (after `fork`, before `exec`).
#[cfg(target_os = "linux")]
pub unsafe fn install_child_network_filter() -> std::io::Result<()> {
    use libc::{
        BPF_ABS, BPF_JEQ, BPF_JMP, BPF_K, BPF_LD, BPF_RET, BPF_W, PR_SET_NO_NEW_PRIVS,
        PR_SET_SECCOMP, SECCOMP_MODE_FILTER, SECCOMP_RET_KILL_PROCESS, prctl, sock_filter,
        sock_fprog,
    };

    const SECCOMP_RET_ALLOW: u32 = 0x7fff_0000;
    const SECCOMP_RET_ERRNO: u32 = 0x0005_0000;
    const EPERM_VAL: u32 = 1; // libc::EPERM

    macro_rules! bpf_stmt {
        ($code:expr, $k:expr) => {
            sock_filter {
                code: $code as u16,
                jt: 0,
                jf: 0,
                k: $k as u32,
            }
        };
    }

    macro_rules! bpf_jump {
        ($code:expr, $k:expr, $jt:expr, $jf:expr) => {
            sock_filter {
                code: $code as u16,
                jt: $jt,
                jf: $jf,
                k: $k as u32,
            }
        };
    }

    const NR_OFFSET: u32 = 0;
    const ARCH_OFFSET: u32 = 4;
    let audit_arch = native_audit_arch().ok_or_else(|| {
        std::io::Error::new(
            std::io::ErrorKind::Unsupported,
            "child network filter does not support this Linux architecture",
        )
    })?;
    let blocked_syscalls = blocked_network_syscalls();

    let mut filter: Vec<sock_filter> = Vec::new();
    let total_checks = blocked_syscalls.len();

    // Reject a syscall table mismatch before comparing syscall numbers.
    filter.push(bpf_stmt!(BPF_LD | BPF_W | BPF_ABS, ARCH_OFFSET));
    filter.push(bpf_jump!(BPF_JMP | BPF_JEQ | BPF_K, audit_arch, 1, 0));
    filter.push(bpf_stmt!(BPF_RET | BPF_K, SECCOMP_RET_KILL_PROCESS));

    // 1. Load syscall number.
    filter.push(bpf_stmt!(BPF_LD | BPF_W | BPF_ABS, NR_OFFSET));

    // 2. Check each blocked syscall
    for (i, &syscall) in blocked_syscalls.iter().enumerate() {
        let remaining = total_checks - i - 1;
        filter.push(bpf_jump!(
            BPF_JMP | BPF_JEQ | BPF_K,
            syscall,
            remaining as u8 + 1, // match: jump to ERRNO
            0                    // no match: check next
        ));
    }

    // 3. Default: ALLOW
    filter.push(bpf_stmt!(BPF_RET | BPF_K, SECCOMP_RET_ALLOW));

    // 4. Blocked: ERRNO(EPERM)
    filter.push(bpf_stmt!(BPF_RET | BPF_K, SECCOMP_RET_ERRNO | EPERM_VAL));

    let prog = sock_fprog {
        len: filter.len() as u16,
        filter: filter.as_mut_ptr(),
    };

    // Must set PR_SET_NO_NEW_PRIVS before applying seccomp filter
    // SAFETY: prctl with PR_SET_NO_NEW_PRIVS is safe in pre_exec context.
    if unsafe { prctl(PR_SET_NO_NEW_PRIVS, 1, 0, 0, 0) } != 0 {
        return Err(std::io::Error::last_os_error());
    }

    // SAFETY: prog is a valid sock_fprog pointing to our filter array.
    if unsafe {
        prctl(
            PR_SET_SECCOMP,
            SECCOMP_MODE_FILTER as libc::c_ulong,
            &prog as *const _ as libc::c_ulong,
            0,
            0,
        )
    } != 0
    {
        return Err(std::io::Error::last_os_error());
    }

    Ok(())
}

/// # Safety
///
/// No-op on non-Linux.
#[cfg(not(target_os = "linux"))]
pub unsafe fn install_child_network_filter() -> std::io::Result<()> {
    Ok(())
}

#[cfg(all(test, target_os = "linux"))]
mod tests {
    use super::blocked_network_syscalls;

    #[test]
    fn filter_blocks_socket_sendmmsg_and_io_uring_bypasses() {
        let blocked = blocked_network_syscalls();

        for syscall in [
            libc::SYS_socket,
            libc::SYS_socketpair,
            libc::SYS_sendmmsg,
            libc::SYS_io_uring_setup,
            libc::SYS_io_uring_enter,
            libc::SYS_io_uring_register,
        ] {
            assert!(
                blocked.contains(&syscall),
                "network filter must block syscall {syscall}"
            );
        }
    }
}
