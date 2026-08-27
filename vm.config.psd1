@{
    Name             = 'agent-dev'
    Image            = '24.04'
    Cpus             = 4
    Memory           = '4G'
    Disk             = '50G'
    TimeoutSeconds   = 1800
    StorageDirectory = '.multipass-data'
    BaselineSnapshot = 'clean'
}
