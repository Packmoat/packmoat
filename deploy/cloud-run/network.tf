# Only with enable_static_egress (off by default: Cloud NAT and a static address cost money every month): a VPC with one subnet the
# server's Direct VPC egress uses, a Cloud Router, and Cloud NAT that sends everything out from one static external address.

resource "google_compute_network" "egress" {
  count = var.enable_static_egress ? 1 : 0

  name                    = "${var.name}-egress"
  auto_create_subnetworks = false
}

resource "google_compute_subnetwork" "egress" {
  count = var.enable_static_egress ? 1 : 0

  name                     = "${var.name}-egress"
  region                   = var.region
  network                  = google_compute_network.egress[0].id
  ip_cidr_range            = var.egress_subnet_cidr
  private_ip_google_access = true
}

resource "google_compute_router" "egress" {
  count = var.enable_static_egress ? 1 : 0

  name    = "${var.name}-egress"
  region  = var.region
  network = google_compute_network.egress[0].id
}

resource "google_compute_address" "egress" {
  count = var.enable_static_egress ? 1 : 0

  name         = "${var.name}-egress"
  region       = var.region
  address_type = "EXTERNAL"
}

resource "google_compute_router_nat" "egress" {
  count = var.enable_static_egress ? 1 : 0

  name   = "${var.name}-egress"
  region = var.region
  router = google_compute_router.egress[0].name

  # Only the static address: never one Google picks.
  nat_ip_allocate_option             = "MANUAL_ONLY"
  nat_ips                            = [google_compute_address.egress[0].self_link]
  source_subnetwork_ip_ranges_to_nat = "LIST_OF_SUBNETWORKS"

  subnetwork {
    name                    = google_compute_subnetwork.egress[0].id
    source_ip_ranges_to_nat = ["ALL_IP_RANGES"]
  }
}
