CC = mcs
CFLAGS = -pkg:dotnet

main:
	$(CC) ImgurCrawler.cs $(CFLAGS)

.PHONY: clean
clean:
	rm -f ImgurCrawler.exe
