variable "project_id" {
  description = "GCP project ID."
  type        = string
}

variable "region" {
  description = "Region. Pick the one closest to you — streaming latency is dominated by physical distance."
  type        = string
  default     = "us-central1"
}

variable "zone" {
  description = "Zone. Must have your chosen GPU available (see: gcloud compute accelerator-types list)."
  type        = string
  default     = "us-central1-a"
}

variable "machine_type" {
  description = <<-EOT
    g2-standard-8  = 8 vCPU / 32 GB / 1x NVIDIA L4  (recommended: L4 handles CS2 well,
                     and 32 GB matters for the large-city benchmarks this repo cares about)
    n1-standard-8  = 8 vCPU / 30 GB, pair with gpu_type = "nvidia-tesla-t4-vws" (cheaper, slower)
  EOT
  type        = string
  default     = "g2-standard-8"
}

variable "gpu_type" {
  description = <<-EOT
    Leave EMPTY for g2-* machine types: the L4 is part of the machine type and must NOT
    also be requested as a guest accelerator. Set to "nvidia-tesla-t4-vws" (or another
    -vws SKU) only when using an n1-* machine type.

    NOTE: the "-vws" suffix requests an NVIDIA RTX Virtual Workstation licence, which is
    what makes DirectX / OpenGL work. G2 + L4 vWS licensing is applied differently and has
    changed over time — verify against current GCP docs before your first apply.
  EOT
  type        = string
  default     = ""
}

variable "gpu_count" {
  type    = number
  default = 1
}

variable "use_spot" {
  description = <<-EOT
    Spot cuts compute cost ~60-70% but GCP can reclaim the VM with 30s notice.
    Good for the mod dev loop (build, launch, export, exit). Annoying mid-play-session,
    though CS2 autosaves. Start with false; flip to true once you trust your save cadence.
  EOT
  type        = bool
  default     = false
}

variable "boot_disk_gb" {
  description = "Windows Server needs ~50 GB; 60 leaves room for updates."
  type        = number
  default     = 60
}

variable "boot_disk_type" {
  type    = string
  default = "pd-balanced"
}

variable "data_disk_gb" {
  description = "Steam library + CS2 (~60 GB) + repo + toolchain. 150 is comfortable."
  type        = number
  default     = 150
}

variable "data_disk_type" {
  description = "pd-balanced for responsiveness; pd-standard is ~60% cheaper but load times suffer."
  type        = string
  default     = "pd-balanced"
}

variable "data_disk_source_snapshot" {
  description = <<-EOT
    Set to a snapshot name to restore the game disk instead of creating an empty one.
    This is how `cs2 wake` brings the machine back after `cs2 hibernate` deleted the disks.
  EOT
  type        = string
  default     = ""
}

variable "my_ip_cidr" {
  description = <<-EOT
    Your public IP as a /32, e.g. "203.0.113.7/32". Used to scope RDP and the Parsec
    UDP range. Find it with: curl -s ifconfig.me
    Deliberately has no default — an open 3389 on a Windows box is a bad afternoon.
  EOT
  type        = string
}

variable "repo_url" {
  description = "Repo the VM clones for the mod build loop."
  type        = string
  default     = "https://github.com/seidelap/cities-skylines-2-path-optimization.git"
}

variable "repo_branch" {
  type    = string
  default = "claude/cities-skylines-pathfinding-mod-v7rs3h"
}

variable "idle_shutdown_minutes" {
  description = "Consecutive idle minutes before the VM shuts itself down. 0 disables."
  type        = number
  default     = 20
}

variable "max_session_hours" {
  description = <<-EOT
    Hard cap: the VM shuts down after this many hours of uptime no matter what.
    This is the runaway-cost backstop — the thing that saves you if the idle
    detector is fooled by a stuck process. Do not set it to 0.
  EOT
  type        = number
  default     = 6
}

variable "billing_account" {
  description = "Optional billing account ID for a budget alert. Empty disables the budget resource."
  type        = string
  default     = ""
}

variable "monthly_budget_usd" {
  type    = number
  default = 75
}
