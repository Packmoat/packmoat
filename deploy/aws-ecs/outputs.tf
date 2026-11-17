# The outputs. None is sensitive and none names a secret.

output "load_balancer_dns_name" {
  description = "The load balancer's own DNS name. Point your host names at it (an alias record, or a CNAME) (docs/deploy/aws-ecs.md)."
  value       = aws_lb.main.dns_name
}

output "load_balancer_zone_id" {
  description = "The load balancer's hosted zone, for a Route 53 alias record."
  value       = aws_lb.main.zone_id
}

output "task_security_group_id" {
  description = "The tasks' security group: admit it on 5432 in your database's own security group."
  value       = aws_security_group.task.id
}

output "migrate_run_task_command" {
  description = "The AWS CLI command that runs the migrate task once, in the tasks' subnets and security group. Run it before each upgrade of service_image, and wait for the task to stop with exit code 0."
  value = join(" ", [
    "aws ecs run-task",
    "--region ${var.region}",
    "--cluster ${aws_ecs_cluster.main.name}",
    "--launch-type FARGATE",
    "--task-definition ${aws_ecs_task_definition.migrate.arn}",
    "--network-configuration 'awsvpcConfiguration={subnets=[${join(",", local.task_subnets)}],securityGroups=[${aws_security_group.task.id}],assignPublicIp=${local.task_public_address ? "ENABLED" : "DISABLED"}}'",
  ])
}

output "outbound_ip_address" {
  description = "The fixed outbound address, when enable_nat_gateway is true; empty otherwise (each task then goes out from its own public address)."
  value       = var.enable_nat_gateway ? aws_eip.nat[0].public_ip : ""
}
