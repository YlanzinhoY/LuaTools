//go:build !windows

package main

import "syscall"

func hiddenWindowAttributes() *syscall.SysProcAttr { return nil }
