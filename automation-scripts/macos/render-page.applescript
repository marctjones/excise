property pdfePath : "/usr/local/bin/pdfe"

on run argv
    if (count of argv) < 2 then
        error "Usage: osascript render-page.applescript input.pdf output.png [page] [dpi]"
    end if

    set inputPdf to item 1 of argv
    set outputPng to item 2 of argv
    set pageNumber to "1"
    set renderDpi to "150"

    if (count of argv) >= 3 then set pageNumber to item 3 of argv
    if (count of argv) >= 4 then set renderDpi to item 4 of argv

    set commandLine to quoted form of pdfePath & " render " & quoted form of inputPdf & " --output " & quoted form of outputPng & " --page " & pageNumber & " --dpi " & renderDpi & " --json"
    return do shell script commandLine
end run
