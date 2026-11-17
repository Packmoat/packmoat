# The Application Load Balancer in the public subnets: HTTPS on 443 with your ACM certificate, HTTP on 80 only to send clients to HTTPS,
# and the tasks behind it on 8080. Two security groups, each rule a resource of its own: the load balancer's takes 80 and 443 from anywhere
# and may only reach the tasks on 8080; the tasks' takes 8080 from the load balancer's group alone, and may only reach HTTPS anywhere (the
# image registry, Secrets Manager, CloudWatch Logs, nuget.org, Azure DevOps, Microsoft Entra) and PostgreSQL in database_cidr.

resource "aws_security_group" "alb" {
  name        = "${var.name}-alb"
  description = "Packmoat load balancer: HTTP and HTTPS in, the tasks on 8080 out."
  vpc_id      = var.vpc_id
}

resource "aws_vpc_security_group_ingress_rule" "alb_https" {
  security_group_id = aws_security_group.alb.id
  description       = "HTTPS from anywhere."
  ip_protocol       = "tcp"
  from_port         = 443
  to_port           = 443
  cidr_ipv4         = "0.0.0.0/0"
}

resource "aws_vpc_security_group_ingress_rule" "alb_http" {
  security_group_id = aws_security_group.alb.id
  description       = "HTTP from anywhere, answered with a redirect to HTTPS."
  ip_protocol       = "tcp"
  from_port         = 80
  to_port           = 80
  cidr_ipv4         = "0.0.0.0/0"
}

resource "aws_vpc_security_group_egress_rule" "alb_to_tasks" {
  security_group_id            = aws_security_group.alb.id
  description                  = "To the tasks on 8080 only."
  ip_protocol                  = "tcp"
  from_port                    = 8080
  to_port                      = 8080
  referenced_security_group_id = aws_security_group.task.id
}

resource "aws_security_group" "task" {
  name        = "${var.name}-task"
  description = "Packmoat tasks: 8080 from the load balancer only; HTTPS and PostgreSQL out."
  vpc_id      = var.vpc_id
}

resource "aws_vpc_security_group_ingress_rule" "task_from_alb" {
  security_group_id            = aws_security_group.task.id
  description                  = "8080 from the load balancer only."
  ip_protocol                  = "tcp"
  from_port                    = 8080
  to_port                      = 8080
  referenced_security_group_id = aws_security_group.alb.id
}

resource "aws_vpc_security_group_egress_rule" "task_https" {
  security_group_id = aws_security_group.task.id
  description       = "HTTPS out: the image registry, AWS APIs, nuget.org, Azure DevOps and Microsoft Entra."
  ip_protocol       = "tcp"
  from_port         = 443
  to_port           = 443
  cidr_ipv4         = "0.0.0.0/0"
}

resource "aws_vpc_security_group_egress_rule" "task_database" {
  security_group_id = aws_security_group.task.id
  description       = "PostgreSQL out, to the database's network only."
  ip_protocol       = "tcp"
  from_port         = 5432
  to_port           = 5432
  cidr_ipv4         = var.database_cidr
}

resource "aws_lb" "main" {
  name               = var.name
  load_balancer_type = "application"
  internal           = false
  security_groups    = [aws_security_group.alb.id]
  subnets            = var.public_subnet_ids

  # Headers that are not valid HTTP are dropped, and requests that could be read two ways are refused (the strictest desync mode). The
  # load balancer appends the client's address to X-Forwarded-For, which the server trusts from public_subnet_cidrs.
  drop_invalid_header_fields = true
  desync_mitigation_mode     = "strictest"
  xff_header_processing_mode = "append"

  # tofu destroy refuses the load balancer until this is set to false and applied.
  enable_deletion_protection = true

  depends_on = [terraform_data.checks]
}

resource "aws_lb_target_group" "server" {
  name                 = "${var.name}-server"
  target_type          = "ip"
  protocol             = "HTTP"
  port                 = 8080
  vpc_id               = var.vpc_id
  deregistration_delay = 30

  # /readyz answers 200 only when the database opens, is at the server's schema and the master key matches it. The health check sends the
  # task's own address as its Host (not yet verified), which the server answers on /readyz.
  health_check {
    protocol            = "HTTP"
    path                = "/readyz"
    port                = "traffic-port"
    matcher             = "200"
    interval            = 15
    timeout             = 5
    healthy_threshold   = 2
    unhealthy_threshold = 3
  }
}

resource "aws_lb_listener" "https" {
  load_balancer_arn = aws_lb.main.arn
  port              = 443
  protocol          = "HTTPS"
  ssl_policy        = "ELBSecurityPolicy-TLS13-1-2-2021-06"
  certificate_arn   = var.certificate_arn

  default_action {
    type             = "forward"
    target_group_arn = aws_lb_target_group.server.arn
  }
}

resource "aws_lb_listener" "http" {
  load_balancer_arn = aws_lb.main.arn
  port              = 80
  protocol          = "HTTP"

  default_action {
    type = "redirect"

    redirect {
      protocol    = "HTTPS"
      port        = "443"
      status_code = "HTTP_301"
    }
  }
}
