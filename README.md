# DropboxEncryptedUploader
Synchronizes local folder to Dropbox encrypting each file to a separate zip archive. Works in memory without storing to disk. Each file is devided by 140mb blocks, each block is encrypted on the fly and immediately sent to Dropbox servers so the whole encrypted archive (be it even 300 GB) will not take more than just 140mb of RAM.
## Usage
DropboxEncryptedUploader.exe dropbox-app-token path-to-folder dropbox-folder-name encryption-password
### Example
DropboxEncryptedUploader.exe "asdlakdfkfrefggfdgdfg-rgedfgd-adfsfdf3e" "d:\Backups" "/Backups" "password"

## How to get Dropbox token
http://99rabbits.com/get-dropbox-access-token/
