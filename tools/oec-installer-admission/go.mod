module example.com/suprachat/oec-installer-admission

go 1.23

require (
  example.com/suprachat/provision v0.0.0
  example.com/suprachat/oec-tunnel-bootstrap v0.0.0
)
replace example.com/suprachat/provision => ../suprachat-provision
replace example.com/suprachat/oec-tunnel-bootstrap => ../oec-tunnel-bootstrap
