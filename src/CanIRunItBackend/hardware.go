package main

import (
	"context"
	"encoding/json"
	"math"
	"os/exec"
	"runtime"
	"strings"
)

const hardwarePowerShell = `$ErrorActionPreference='Stop'
$cpu = Get-CimInstance Win32_Processor | Select-Object -First 1
$videoMemory = @{}
Get-ChildItem 'HKLM:\SYSTEM\CurrentControlSet\Control\Video' -ErrorAction SilentlyContinue | ForEach-Object {
  Get-ChildItem $_.PSPath -ErrorAction SilentlyContinue
} | ForEach-Object {
  $properties = Get-ItemProperty $_.PSPath -ErrorAction SilentlyContinue
  $adapterName = [string]$properties.DriverDesc
  if (-not $adapterName) { $adapterName = [string]$properties.'HardwareInformation.AdapterString' }
  try { $memory = [int64]$properties.'HardwareInformation.qwMemorySize' } catch { $memory = 0 }
  if ($adapterName -and $memory -gt 0 -and ((-not $videoMemory.ContainsKey($adapterName)) -or $memory -gt $videoMemory[$adapterName])) {
    $videoMemory[$adapterName] = $memory
  }
}
$gpus = @(Get-CimInstance Win32_VideoController | Where-Object { $_.Name -and $_.Name -notmatch 'Remote Display|Basic Display' } | ForEach-Object {
  $name = [string]$_.Name
  $vram = [int64]$_.AdapterRAM
  if ($videoMemory.ContainsKey($name)) { $vram = [int64]$videoMemory[$name] }
  elseif ($vram -ge 4200000000 -and $vram -le 4294967295) { $vram = 0 }
  [pscustomobject]@{ name=$name; vram_bytes=$vram; driver_version=[string]$_.DriverVersion }
})
$computer = Get-CimInstance Win32_ComputerSystem
$os = Get-CimInstance Win32_OperatingSystem
$drive = Get-CimInstance Win32_LogicalDisk -Filter ("DeviceID='" + $env:SystemDrive + "'")
[pscustomobject]@{
  cpu=[string]$cpu.Name
  logical_processors=[int]$cpu.NumberOfLogicalProcessors
  max_clock_mhz=[int]$cpu.MaxClockSpeed
  memory_bytes=[int64]$computer.TotalPhysicalMemory
  gpus=$gpus
  os=(([string]$os.Caption) + ' ' + ([string]$os.Version) + ' ' + ([string]$os.OSArchitecture)).Trim()
  system_drive_free_bytes=[int64]$drive.FreeSpace
} | ConvertTo-Json -Depth 4 -Compress`

type rawHardware struct {
	CPU                  string `json:"cpu"`
	LogicalProcessors    int    `json:"logical_processors"`
	MaxClockMHz          int    `json:"max_clock_mhz"`
	MemoryBytes          int64  `json:"memory_bytes"`
	OS                   string `json:"os"`
	SystemDriveFreeBytes int64  `json:"system_drive_free_bytes"`
	GPUs                 []struct {
		Name          string `json:"name"`
		VRAMBytes     int64  `json:"vram_bytes"`
		DriverVersion string `json:"driver_version"`
	} `json:"gpus"`
}

func detectHardware(ctx context.Context) (hardwareInfo, *backendError) {
	if runtime.GOOS != "windows" {
		return hardwareInfo{}, &backendError{Code: "unsupported_os", Message: "Hardware detection is currently available on Windows only."}
	}
	command := exec.CommandContext(ctx, "powershell.exe", "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-Command", hardwarePowerShell)
	command.SysProcAttr = hiddenWindowAttributes()
	output, err := command.Output()
	if err != nil {
		return hardwareInfo{}, &backendError{Code: "hardware_detection_failed", Message: "Windows could not read this PC's CPU, GPU, memory, and OS information."}
	}

	output = []byte(strings.TrimPrefix(strings.TrimSpace(string(output)), "\ufeff"))
	var raw rawHardware
	if err := json.Unmarshal(output, &raw); err != nil || strings.TrimSpace(raw.CPU) == "" || raw.MemoryBytes <= 0 {
		return hardwareInfo{}, &backendError{Code: "hardware_detection_failed", Message: "Windows returned incomplete hardware information."}
	}

	result := hardwareInfo{
		CPU:               strings.TrimSpace(raw.CPU),
		LogicalProcessors: raw.LogicalProcessors,
		MaxClockMHz:       raw.MaxClockMHz,
		MemoryGB:          roundGB(raw.MemoryBytes),
		OS:                strings.TrimSpace(raw.OS),
		SystemDriveFreeGB: roundGB(raw.SystemDriveFreeBytes),
		GPUs:              make([]gpuInfo, 0, len(raw.GPUs)),
	}
	for _, gpu := range raw.GPUs {
		name := strings.TrimSpace(gpu.Name)
		if name == "" {
			continue
		}
		result.GPUs = append(result.GPUs, gpuInfo{
			Name:          name,
			VRAMGB:        roundGB(gpu.VRAMBytes),
			DriverVersion: strings.TrimSpace(gpu.DriverVersion),
		})
	}
	if len(result.GPUs) == 0 {
		result.GPUs = []gpuInfo{{Name: "Unknown GPU"}}
	}
	return result, nil
}

func roundGB(bytes int64) float64 {
	if bytes <= 0 {
		return 0
	}
	return math.Round((float64(bytes)/(1024*1024*1024))*10) / 10
}
