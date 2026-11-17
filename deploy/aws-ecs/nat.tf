# Only with enable_nat_gateway (off by default: a NAT gateway and its Elastic IP cost money every hour): an Elastic IP, a NAT gateway in the
# first public subnet, and private subnets whose one route out is through it. The tasks then run in the private subnets with no public
# address, and everything they send out leaves from the Elastic IP.

resource "aws_eip" "nat" {
  count = var.enable_nat_gateway ? 1 : 0

  domain = "vpc"
  tags   = { Name = "${var.name}-nat" }
}

resource "aws_nat_gateway" "nat" {
  count = var.enable_nat_gateway ? 1 : 0

  allocation_id = aws_eip.nat[0].id
  subnet_id     = var.public_subnet_ids[0]
  tags          = { Name = "${var.name}-nat" }

  depends_on = [terraform_data.checks]
}

# One subnet per private_subnet_cidrs entry, in the zone at the same place in private_subnet_azs (main.tf checks that both lists have the
# same length).
resource "aws_subnet" "private" {
  count = var.enable_nat_gateway ? length(var.private_subnet_cidrs) : 0

  vpc_id                  = var.vpc_id
  cidr_block              = var.private_subnet_cidrs[count.index]
  availability_zone       = element(var.private_subnet_azs, count.index)
  map_public_ip_on_launch = false
  tags                    = { Name = "${var.name}-private-${count.index}" }

  depends_on = [terraform_data.checks]
}

resource "aws_route_table" "private" {
  count = var.enable_nat_gateway ? 1 : 0

  vpc_id = var.vpc_id
  tags   = { Name = "${var.name}-private" }
}

resource "aws_route" "private_nat" {
  count = var.enable_nat_gateway ? 1 : 0

  route_table_id         = aws_route_table.private[0].id
  destination_cidr_block = "0.0.0.0/0"
  nat_gateway_id         = aws_nat_gateway.nat[0].id
}

resource "aws_route_table_association" "private" {
  count = var.enable_nat_gateway ? length(var.private_subnet_cidrs) : 0

  subnet_id      = aws_subnet.private[count.index].id
  route_table_id = aws_route_table.private[0].id
}
