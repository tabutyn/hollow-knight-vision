$ErrorActionPreference = 'Stop'

# Signed tessdata_fast 4.1.0, pinned to the release commit.
$upstream = 'https://raw.githubusercontent.com/tesseract-ocr/tessdata_fast/65727574dfcd264acbb0c3e07860e4e9e9b22185'
$files = @{
    'chi_sim.traineddata' = 'a5fcb6f0db1e1d6d8522f39db4e848f05984669172e584e8d76b6b3141e1f730'
    'chi_tra.traineddata' = '529c5b5797d64b126065cd55f2bb4c7fd7b15790798091b1ff259941a829330b'
    'deu.traineddata' = '19d219bbb6672c869d20a9636c6816a81eb9a71796cb93ebe0cb1530e2cdb22d'
    'eng.traineddata' = '7d4322bd2a7749724879683fc3912cb542f19906c83bcc1a52132556427170b2'
    'fra.traineddata' = 'ced037562e8c80c13122dece28dd477d399af80911a28791a66a63ac1e3445ca'
    'ita.traineddata' = 'b8f89e1e785118dac4d51ae042c029a64edb5c3ee42ef73027a6d412748d8827'
    'jpn.traineddata' = '1f5de9236d2e85f5fdf4b3c500f2d4926f8d9449f28f5394472d9e8d83b91b4d'
    'kor.traineddata' = '6b85e11d9bbf07863b97b3523b1b112844c43e713df8b66418a081fd1060b3b2'
    'por.traineddata' = 'c4932b937207a9514b7514d518b931a99938c02a28a5a5a553f8599ed58b7deb'
    'rus.traineddata' = 'e16e5e036cce1d9ec2b00063cf8b54472625b9e14d893a169e2b0dedeb4df225'
    'spa.traineddata' = '6f2e04d02774a18f01bed44b1111f2cd7f3ba7ac9dc4373cd3f898a40ea6b464'
    'LICENSE' = 'cfc7749b96f63bd31c3c42b5c471bf756814053e847c10f3eb003417bc523d30'
}
$requestOptions = @{ UseBasicParsing = $true; TimeoutSec = 45 }
if ((Get-Command Invoke-WebRequest).Parameters.ContainsKey('MaximumRetryCount')) {
    $requestOptions.MaximumRetryCount = 2
    $requestOptions.RetryIntervalSec = 2
}
$directory = (New-Item -ItemType Directory -Path (Join-Path $PSScriptRoot 'artifacts/game-text/tessdata') -Force).FullName
foreach ($file in $files.GetEnumerator()) {
    $path = Join-Path $directory $file.Key
    if ((Test-Path -LiteralPath $path -PathType Leaf) -and (Get-FileHash -LiteralPath $path).Hash -eq $file.Value) {
        continue
    }
    $download = "$path.$([Guid]::NewGuid().ToString('N')).tmp"
    try {
        Invoke-WebRequest -Uri "$upstream/$($file.Key)" -OutFile $download @requestOptions
        if ((Get-FileHash -LiteralPath $download).Hash -ne $file.Value) {
            throw "Incorrect checksum for game text asset $($file.Key)."
        }
        Move-Item -LiteralPath $download -Destination $path -Force
    } finally {
        if (Test-Path -LiteralPath $download) { Remove-Item -LiteralPath $download -Force }
    }
}
