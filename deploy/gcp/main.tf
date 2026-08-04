terraform {
  required_version = ">= 1.5"
  required_providers {
    google = {
      source  = "hashicorp/google"
      version = "~> 5.0"
    }
  }
}

provider "google" {
  project = var.project_id
  region  = var.region
  zone    = var.zone
}

locals {
  name = "cs2-workstation"
  # Startup script is templated so the VM knows its own idle policy and repo.
  startup_ps1 = templatefile("${path.module}/startup.ps1", {
    repo_url              = var.repo_url
    repo_branch           = var.repo_branch
    idle_shutdown_minutes = var.idle_shutdown_minutes
    max_session_hours     = var.max_session_hours
    export_bucket         = google_storage_bucket.exports.name
    idle_guard_ps1        = file("${path.module}/idle-guard.ps1")
  })
}

# ---------------------------------------------------------------------------
# Network: default VPC is fine; we only need two scoped ingress rules.
# ---------------------------------------------------------------------------
resource "google_compute_firewall" "rdp" {
  name          = "${local.name}-rdp"
  network       = "default"
  description   = "RDP for first-time setup (Steam + Parsec logins). Scoped to your IP."
  source_ranges = [var.my_ip_cidr]
  target_tags   = [local.name]

  allow {
    protocol = "tcp"
    ports    = ["3389"]
  }
}

resource "google_compute_firewall" "parsec" {
  name          = "${local.name}-parsec"
  network       = "default"
  description   = "Parsec host UDP range. Parsec usually punches through outbound, but this removes a class of 'it just won't connect' evenings."
  source_ranges = [var.my_ip_cidr]
  target_tags   = [local.name]

  allow {
    protocol = "udp"
    ports    = ["8000-8040"]
  }
}

# ---------------------------------------------------------------------------
# Identity: the VM needs to write city exports to GCS and nothing else.
# ---------------------------------------------------------------------------
resource "google_service_account" "vm" {
  account_id   = "${local.name}-sa"
  display_name = "CS2 workstation"
}

resource "google_storage_bucket" "exports" {
  name                        = "${var.project_id}-cs2-exports"
  location                    = var.region
  uniform_bucket_level_access = true
  force_destroy               = true

  # Exports are large and reproducible; don't let them quietly accrue cost.
  lifecycle_rule {
    condition { age = 90 }
    action { type = "Delete" }
  }
}

resource "google_storage_bucket_iam_member" "vm_writes_exports" {
  bucket = google_storage_bucket.exports.name
  role   = "roles/storage.objectAdmin"
  member = "serviceAccount:${google_service_account.vm.email}"
}

# ---------------------------------------------------------------------------
# Disks. The game disk is deliberately separate from boot so that:
#   * rebuilding the VM does not mean re-downloading 60 GB of CS2, and
#   * `cs2 hibernate` can snapshot just the disk that carries real state.
# ---------------------------------------------------------------------------
resource "google_compute_disk" "data" {
  name = "${local.name}-data"
  type = var.data_disk_type
  zone = var.zone
  size = var.data_disk_gb

  # When restoring after hibernate, the disk is created from the snapshot.
  snapshot = var.data_disk_source_snapshot != "" ? var.data_disk_source_snapshot : null

  lifecycle {
    # Changing size/type in place is fine; changing the snapshot source is not
    # something we want Terraform doing implicitly to a disk holding your saves.
    prevent_destroy = false
  }
}

resource "google_compute_instance" "vm" {
  name         = local.name
  machine_type = var.machine_type
  zone         = var.zone
  tags         = [local.name]

  # GPU VMs cannot live-migrate: they must terminate on host maintenance.
  scheduling {
    on_host_maintenance         = "TERMINATE"
    automatic_restart           = !var.use_spot
    provisioning_model          = var.use_spot ? "SPOT" : "STANDARD"
    preemptible                 = var.use_spot
    instance_termination_action = var.use_spot ? "STOP" : null
  }

  # Only for n1-* + a "-vws" accelerator. g2-* machine types bundle their L4 and
  # must leave gpu_type empty.
  dynamic "guest_accelerator" {
    for_each = var.gpu_type == "" ? [] : [1]
    content {
      type  = var.gpu_type
      count = var.gpu_count
    }
  }

  boot_disk {
    initialize_params {
      image = "projects/windows-cloud/global/images/family/windows-2022"
      size  = var.boot_disk_gb
      type  = var.boot_disk_type
    }
  }

  attached_disk {
    source      = google_compute_disk.data.id
    device_name = "cs2data"
  }

  network_interface {
    network = "default"
    access_config {
      # Ephemeral IP: a reserved static IP costs money while the VM is stopped,
      # and Parsec does not need a stable address.
    }
  }

  service_account {
    email  = google_service_account.vm.email
    scopes = ["https://www.googleapis.com/auth/devstorage.read_write"]
  }

  metadata = {
    windows-startup-script-ps1 = local.startup_ps1
    export-bucket              = google_storage_bucket.exports.name
    enable-oslogin             = "FALSE"
  }

  # `cs2 up`/`cs2 down` flip the VM's power state outside Terraform; don't fight it.
  lifecycle {
    ignore_changes = [desired_status]
  }

  allow_stopping_for_update = true
  deletion_protection       = false
}

# ---------------------------------------------------------------------------
# Budget alert (optional): the cheapest insurance in cloud gaming.
# ---------------------------------------------------------------------------
resource "google_billing_budget" "cs2" {
  count           = var.billing_account == "" ? 0 : 1
  billing_account = var.billing_account
  display_name    = "CS2 workstation"

  budget_filter {
    projects = ["projects/${var.project_id}"]
  }

  amount {
    specified_amount {
      currency_code = "USD"
      units         = tostring(var.monthly_budget_usd)
    }
  }

  threshold_rules { threshold_percent = 0.5 }
  threshold_rules { threshold_percent = 0.9 }
  threshold_rules { threshold_percent = 1.0 }
}
