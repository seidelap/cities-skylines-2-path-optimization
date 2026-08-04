output "instance_name" {
  value = google_compute_instance.vm.name
}

output "zone" {
  value = var.zone
}

output "external_ip" {
  description = "Ephemeral — changes on every start. `cs2 status` reads the current one."
  value       = try(google_compute_instance.vm.network_interface[0].access_config[0].nat_ip, "(stopped)")
}

output "export_bucket" {
  value = google_storage_bucket.exports.name
}

output "data_disk" {
  value = google_compute_disk.data.name
}

output "first_login" {
  description = "Run this to mint a Windows password, then RDP in once to sign into Steam and Parsec."
  value       = "gcloud compute reset-windows-password ${google_compute_instance.vm.name} --zone ${var.zone} --user cs2"
}
