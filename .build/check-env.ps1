Write-Output ('CommonApplicationData=[' + [Environment]::GetFolderPath('CommonApplicationData') + ']')
Write-Output ('ProgramData env=[' + $env:ProgramData + ']')
Write-Output ('ProgramFilesX86=[' + [Environment]::GetFolderPath('ProgramFilesX86') + ']')
