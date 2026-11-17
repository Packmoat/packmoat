# The outputs. None is sensitive and none names a secret.

output "service_uri" {
  description = "The service's own address on Cloud Run (*.run.app). Map your host names to the service (docs/deploy/cloud-run.md)."
  value       = google_cloud_run_v2_service.server.uri
}

output "migrate_job_name" {
  description = "The migrate job's name, for gcloud run jobs execute."
  value       = google_cloud_run_v2_job.migrate.name
}

output "retention_job_name" {
  description = "The retention job's name. Cloud Scheduler starts it daily; gcloud run jobs execute runs it by hand."
  value       = google_cloud_run_v2_job.retention.name
}

output "outbound_ip_address" {
  description = "The fixed outbound address, when enable_static_egress is true; empty otherwise."
  value       = var.enable_static_egress ? google_compute_address.egress[0].address : ""
}
